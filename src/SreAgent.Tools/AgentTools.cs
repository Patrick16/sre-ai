using System.ComponentModel;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SreAgent.Core;

namespace SreAgent.Tools;

/// <summary>
/// The read-only tools the model may call. Each returns compact text, not raw API JSON: a 9B model reasons far
/// better over "orders-api-x  Running  2/2 ready  0 restarts" than over 3 KB of pod JSON.
/// The [Description]s are what the model reads to decide which tool to use - they are part of the prompt.
/// </summary>
public sealed class AgentTools(IKubernetes kube, PrometheusClient prometheus, LokiClient loki, AlertmanagerClient alertmanager, IOptions<ToolsOptions> options)
    : IAgentToolset
{
    private readonly ToolsOptions _options = options.Value;

    // Interpret, don't just report: in a real run the 9B model saw desired=0 plus a "SuccessfulDelete" pod event and
    // concluded "the pod was deleted". The spec is the cause; saying so is cheaper than hoping the model infers it.
    private const string ScaledToZero =
        "NOTE: scaled to ZERO replicas in its spec (desired=0) - the controller deleted its pods on purpose and will not " +
        "create any until replicas is set above 0. This is a configuration change, not a crash.";

    public IReadOnlyList<AIFunction> Tools =>
    [
        AIFunctionFactory.Create(ListPodsAsync, "list_pods"),
        AIFunctionFactory.Create(DescribeWorkloadAsync, "describe_workload"),
        AIFunctionFactory.Create(GetEventsAsync, "get_events"),
        AIFunctionFactory.Create(QueryMetricsAsync, "query_metrics"),
        AIFunctionFactory.Create(QueryLogsAsync, "query_logs"),
        AIFunctionFactory.Create(GetActiveAlertsAsync, "get_active_alerts"),
    ];

    [Description("List the pods of a namespace: phase, readiness, restarts, last termination reason (e.g. OOMKilled), age, node.")]
    public async Task<string> ListPodsAsync([Description("Kubernetes namespace, e.g. sandbox")] string @namespace, CancellationToken ct)
    {
        _options.EnsureAllowed(@namespace);
        var pods = await kube.CoreV1.ListNamespacedPodAsync(@namespace, cancellationToken: ct);
        if (pods.Items.Count == 0) return $"no pods in {@namespace}";
        var sb = new StringBuilder();
        foreach (var p in pods.Items.OrderBy(p => p.Metadata.Name))
            sb.AppendLine(PodLine(p));
        return sb.ToString().TrimEnd();
    }

    [Description("Describe a Deployment or StatefulSet: desired/ready replicas, image, conditions and recent rollout history (ReplicaSets).")]
    public async Task<string> DescribeWorkloadAsync(
        [Description("Kubernetes namespace")] string @namespace,
        [Description("Workload name, e.g. orders-api or postgres")] string name,
        CancellationToken ct)
    {
        _options.EnsureAllowed(@namespace);
        var sb = new StringBuilder();
        try
        {
            var d = await kube.AppsV1.ReadNamespacedDeploymentAsync(name, @namespace, cancellationToken: ct);
            sb.AppendLine($"Deployment {name}: desired={d.Spec.Replicas} ready={d.Status.ReadyReplicas ?? 0} updated={d.Status.UpdatedReplicas ?? 0} available={d.Status.AvailableReplicas ?? 0}");
            if (d.Spec.Replicas == 0) sb.AppendLine(ScaledToZero);
            sb.AppendLine($"image: {string.Join(", ", d.Spec.Template.Spec.Containers.Select(c => c.Image))}");
            foreach (var c in d.Status.Conditions ?? [])
                sb.AppendLine($"condition {c.Type}={c.Status} ({c.Reason}): {c.Message}");
            var rs = await kube.AppsV1.ListNamespacedReplicaSetAsync(@namespace, labelSelector: $"app.kubernetes.io/name={name}", cancellationToken: ct);
            sb.AppendLine("rollout history (newest first):");
            foreach (var r in rs.Items.OrderByDescending(r => r.Metadata.CreationTimestamp).Take(5))
            {
                var revision = r.Metadata.Annotations is { } ann && ann.TryGetValue("deployment.kubernetes.io/revision", out var rev) ? rev : "?";
                sb.AppendLine($"  {r.Metadata.Name} revision={revision} replicas={r.Status.Replicas} created={r.Metadata.CreationTimestamp:u} image={r.Spec.Template.Spec.Containers.FirstOrDefault()?.Image}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        try
        {
            var s = await kube.AppsV1.ReadNamespacedStatefulSetAsync(name, @namespace, cancellationToken: ct);
            sb.AppendLine($"StatefulSet {name}: desired={s.Spec.Replicas} ready={s.Status.ReadyReplicas ?? 0} current={s.Status.CurrentReplicas ?? 0}");
            if (s.Spec.Replicas == 0) sb.AppendLine(ScaledToZero);
            sb.AppendLine($"image: {string.Join(", ", s.Spec.Template.Spec.Containers.Select(c => c.Image))}");
            foreach (var c in s.Status.Conditions ?? [])
                sb.AppendLine($"condition {c.Type}={c.Status} ({c.Reason}): {c.Message}");
            return sb.ToString().TrimEnd();
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return $"no Deployment or StatefulSet named '{name}' in {@namespace}";
        }
    }

    [Description("Recent Kubernetes events of a namespace (scheduling, probes failing, OOM, image pulls...), newest first.")]
    public async Task<string> GetEventsAsync(
        [Description("Kubernetes namespace")] string @namespace,
        [Description("Only events of objects whose name starts with this, e.g. orders-api; empty for all")] string? objectName = null,
        [Description("How far back, in minutes (max 120)")] int minutes = 30,
        [Description("Only Warning events")] bool warningsOnly = false,
        CancellationToken ct = default)
    {
        _options.EnsureAllowed(@namespace);
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 120));
        var events = await kube.CoreV1.ListNamespacedEventAsync(@namespace, cancellationToken: ct);
        var lines = events.Items
            .Select(e => (e, at: e.LastTimestamp ?? e.EventTime ?? e.Metadata.CreationTimestamp ?? DateTime.MinValue))
            .Where(x => x.at >= since)
            .Where(x => !warningsOnly || x.e.Type == "Warning")
            .Where(x => string.IsNullOrEmpty(objectName) || (x.e.InvolvedObject.Name ?? "").StartsWith(objectName, StringComparison.Ordinal))
            .OrderByDescending(x => x.at)
            .Take(30)
            .Select(x => $"{x.at:HH:mm:ss}Z {x.e.Type} {x.e.Reason} {x.e.InvolvedObject.Kind}/{x.e.InvolvedObject.Name} (x{x.e.Count ?? 1}): {x.e.Message}")
            .ToList();
        return lines.Count == 0 ? $"no matching events in the last {minutes} min" : string.Join('\n', lines);
    }

    [Description("Run a PromQL instant query against Prometheus. Useful series: slo:sli_error:ratio_rate5m (share of bad requests per slo), " +
                 "http_server_request_duration_seconds_count/_bucket (labels app, http_route, http_response_status_code), pg_up, " +
                 "db_client_operation_duration_seconds_bucket (database query time seen by orders-api), " +
                 "kube_pod_container_status_restarts_total, container_memory_working_set_bytes, container_cpu_cfs_throttled_periods_total, " +
                 "request_total (Linkerd, labels deployment, direction, tls).")]
    public async Task<string> QueryMetricsAsync([Description("PromQL expression, e.g. sum by (app) (rate(http_server_request_duration_seconds_count{namespace=\"sandbox\"}[5m]))")] string promql, CancellationToken ct)
    {
        var (samples, error) = await prometheus.QueryAsync(promql, ct);
        return error is null ? PrometheusClient.Format(samples) : $"query error: {error}";
    }

    [Description("Read recent log lines of our services from Loki (JSON logs; the message text is returned). Newest first.")]
    public async Task<string> QueryLogsAsync(
        [Description("Kubernetes namespace")] string @namespace,
        [Description("Service name (pod label app), e.g. orders-api; empty for all")] string? app = null,
        [Description("Regex of log levels, default Warning|Error|Critical")] string level = "Warning|Error|Critical",
        [Description("Only lines containing this text (case-sensitive); empty for all")] string? contains = null,
        [Description("How far back, in minutes (max 60)")] int minutes = 15,
        [Description("Max lines (max 50)")] int limit = 25,
        CancellationToken ct = default)
    {
        _options.EnsureAllowed(@namespace);
        var selector = new StringBuilder($"{{namespace=\"{@namespace}\"");
        if (!string.IsNullOrWhiteSpace(app)) selector.Append($", app=\"{app}\"");
        if (!string.IsNullOrWhiteSpace(level)) selector.Append($", level=~\"{level}\"");
        selector.Append('}');
        if (!string.IsNullOrEmpty(contains)) selector.Append($" |= `{contains.Replace("`", "")}`");

        var lines = await loki.QueryAsync(selector.ToString(), TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60)), Math.Clamp(limit, 1, 50), ct);
        if (lines.Count == 0) return $"no matching log lines in the last {minutes} min";
        return string.Join('\n', lines.Select(l => $"{l.At:HH:mm:ss}Z {l.Pod} [{l.Level}] {l.Message}"));
    }

    [Description("Alerts firing right now in Alertmanager (not silenced): name, severity, namespace, since when, summary.")]
    public async Task<string> GetActiveAlertsAsync(CancellationToken ct)
    {
        var alerts = await alertmanager.ActiveAsync(ct);
        return alerts.Count == 0
            ? "no alerts are firing"
            : string.Join('\n', alerts.OrderBy(a => a.StartsAt).Select(a => $"[{a.Severity}] {a.Name} ns={a.Namespace} since {a.StartsAt:HH:mm:ss}Z: {a.Summary}"));
    }

    internal static string PodLine(V1Pod p)
    {
        var statuses = p.Status.ContainerStatuses ?? [];
        var ready = statuses.Count(c => c.Ready);
        var restarts = statuses.Sum(c => c.RestartCount);
        // WHEN matters as much as why: lastState survives as long as the pod does. In the Phase 9 game day a "last:
        // OOMKilled" from a scenario 90 minutes earlier made the model blame memory for a database slowdown.
        var last = statuses.Select(c => c.LastState?.Terminated).FirstOrDefault(t => t is not null);
        var lastReason = last is null ? null
            : $"{last.Reason}{(last.FinishedAt is { } at ? $" {(int)(DateTime.UtcNow - at).TotalMinutes} min ago" : "")}";
        var waiting = statuses.Select(c => c.State?.Waiting?.Reason).FirstOrDefault(r => r is not null);
        var age = DateTime.UtcNow - (p.Status.StartTime ?? p.Metadata.CreationTimestamp ?? DateTime.UtcNow);
        return $"{p.Metadata.Name}  {p.Status.Phase}{(waiting is null ? "" : $"/{waiting}")}  ready {ready}/{statuses.Count}  restarts {restarts}" +
               $"{(lastReason is null ? "" : $" (last: {lastReason})")}  age {(int)age.TotalMinutes}m  node {p.Spec.NodeName}";
    }
}
