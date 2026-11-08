using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;
using SreAgent.Core;

namespace SreAgent.Tools;

/// <summary>
/// The only code that changes the cluster. Two actions, both reversible and with a small blast radius:
///   restart_deployment - the same patch `kubectl rollout restart` sends (a pod-template annotation);
///   delete_pod         - one Deployment pod; its ReplicaSet creates a replacement.
/// No scale and no rollback, on purpose: replicas and the pod template belong to Helm. A second writer of those
/// fields would fight the next `helm upgrade` (server-side apply conflicts) - the fix for those is a values change.
/// The cluster enforces the same limits independently: RBAC + ValidatingAdmissionPolicy "sre-agent-actions".
/// </summary>
public sealed class KubernetesActionExecutor(IKubernetes kube, IOptions<ToolsOptions> options, TimeProvider time) : IActionExecutor
{
    public const string ProtectedAnnotation = "sre-agent/protected";
    public const string RestartedAtAnnotation = "kubectl.kubernetes.io/restartedAt";
    public const string FieldManager = "sre-agent";

    public async Task<string?> CheckPreconditionsAsync(ActionProposal p, string ns, CancellationToken ct)
    {
        if (!options.Value.Namespaces.Contains(ns)) return $"namespace '{ns}' is outside the agent's scope";
        try
        {
            return p.Type switch
            {
                ActionCatalog.RestartDeployment => CheckRestart(await kube.AppsV1.ReadNamespacedDeploymentAsync(p.Target, ns, cancellationToken: ct)),
                ActionCatalog.DeletePod => await CheckDeletePodAsync(await kube.CoreV1.ReadNamespacedPodAsync(p.Target, ns, cancellationToken: ct), ns, ct),
                _ => $"unknown action type '{p.Type}'",
            };
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return $"{p.Target} does not exist in {ns}";
        }
    }

    private static string? CheckRestart(V1Deployment d)
    {
        if (IsProtected(d.Metadata)) return $"Deployment {d.Name()} is annotated {ProtectedAnnotation}=true";
        if ((d.Spec.Replicas ?? 1) == 0) return $"Deployment {d.Name()} is scaled to 0 - nothing to restart";
        var rolling = (d.Status.ObservedGeneration ?? 0) < (d.Metadata.Generation ?? 0) || (d.Status.UpdatedReplicas ?? 0) < (d.Spec.Replicas ?? 1);
        return rolling ? $"a rollout of {d.Name()} is already in progress" : null;
    }

    private async Task<string?> CheckDeletePodAsync(V1Pod pod, string ns, CancellationToken ct)
    {
        if (IsProtected(pod.Metadata)) return $"pod {pod.Name()} is annotated {ProtectedAnnotation}=true";
        // Only Deployment pods (owned by a ReplicaSet). A StatefulSet pod (the database) or a bare pod is never "just
        // recreated": deleting postgres-0 is an outage, not a remediation.
        var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
        if (owner?.Kind != "ReplicaSet") return $"pod {pod.Name()} is owned by {owner?.Kind ?? "nothing"}, only Deployment pods may be deleted";
        var rs = await kube.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns, cancellationToken: ct);
        if (IsProtected(rs.Metadata)) return $"ReplicaSet {rs.Name()} is protected";
        var podReady = pod.Status.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
        var readyOthers = (rs.Status.ReadyReplicas ?? 0) - (podReady ? 1 : 0);
        return readyOthers < 1 ? $"deleting {pod.Name()} would leave {owner.Name} without a ready pod" : null;
    }

    public async Task<string> ExecuteAsync(ActionProposal p, string ns, bool dryRun, CancellationToken ct)
    {
        options.Value.EnsureAllowed(ns);
        var dry = dryRun ? "All" : null;
        var prefix = dryRun ? "dry run accepted by the API server - would " : "";
        switch (p.Type)
        {
            case ActionCatalog.RestartDeployment:
                // Strategic-merge patch of ONE annotation, owned by field manager "sre-agent". Helm never sets it, so the
                // next helm upgrade doesn't conflict with it (and the admission policy allows nothing else).
                var patch = new V1Patch(
                    "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":{\"" + RestartedAtAnnotation + "\":\"" +
                    time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}}}}",
                    V1Patch.PatchType.StrategicMergePatch);
                await kube.AppsV1.PatchNamespacedDeploymentAsync(patch, p.Target, ns, dryRun: dry, fieldManager: FieldManager, cancellationToken: ct);
                return $"{prefix}restart Deployment {p.Target} (rolling, pods replaced one by one)";
            case ActionCatalog.DeletePod:
                await kube.CoreV1.DeleteNamespacedPodAsync(p.Target, ns, dryRun: dry, cancellationToken: ct);
                return $"{prefix}delete pod {p.Target} (its ReplicaSet creates a new one)";
            default:
                throw new InvalidOperationException($"unknown action type '{p.Type}'");
        }
    }

    private static bool IsProtected(V1ObjectMeta m) =>
        m.Annotations?.TryGetValue(ProtectedAnnotation, out var v) == true && v.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The runtime kill switch: if ConfigMap <c>sre-agent-kill-switch</c> exists in the agent's namespace, no action runs.
///   kubectl create configmap sre-agent-kill-switch -n sre-agent   # stop all actions now
///   kubectl delete configmap sre-agent-kill-switch -n sre-agent   # allow them again
/// "Exists" rather than "a flag inside": a human in a hurry can't get the value wrong. Fails CLOSED: if the switch
/// can't be read, actions are off.
/// </summary>
public sealed class ConfigMapKillSwitch(IKubernetes kube, IOptions<ToolsOptions> options) : IKillSwitch
{
    public async Task<string?> EngagedAsync(CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            await kube.CoreV1.ReadNamespacedConfigMapAsync(o.KillSwitchConfigMap, o.AgentNamespace, cancellationToken: ct);
            return $"ConfigMap {o.AgentNamespace}/{o.KillSwitchConfigMap} exists - all actions are switched off";
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"cannot read the kill switch ({e.Message}) - failing closed";
        }
    }
}

/// <summary>Before/after reading for effect verification: share of 5xx over 1 minute + pods that aren't ready.</summary>
public sealed class PrometheusHealthProbe(IKubernetes kube, PrometheusClient prometheus, TimeProvider time) : IHealthProbe
{
    public async Task<HealthSnapshot> SnapshotAsync(string ns, CancellationToken ct)
    {
        // 1m, not the SLO's 5m: an effect must be visible a minute or two after the action. Same route filter as the SLIs.
        var sel = $"namespace=\"{ns}\", http_route=~\".+\", http_route!~\"/health.*\"";
        var (samples, error) = await prometheus.QueryAsync(
            $"(sum(rate(http_server_request_duration_seconds_count{{{sel}, http_response_status_code=~\"5..\"}}[1m])) or vector(0))" +
            $" / sum(rate(http_server_request_duration_seconds_count{{{sel}}}[1m]))", ct);
        var ratio = error is null && samples.Count > 0 && double.IsFinite(samples[0].Value) ? samples[0].Value : 0;

        var pods = await kube.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
        var notReady = pods.Items.Count(p => p.Status.Phase != "Succeeded" && p.Status.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") != true);
        return new HealthSnapshot(time.GetUtcNow(), ratio, notReady);
    }
}
