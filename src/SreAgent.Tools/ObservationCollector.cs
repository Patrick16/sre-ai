using System.Text;
using k8s;
using Microsoft.Extensions.Options;
using SreAgent.Core;

namespace SreAgent.Tools;

/// <summary>
/// R of RALF. The same compact snapshot at the start of every iteration, gathered by code: what fired, what is
/// firing now, pods, warning events, SLIs, the database, recent errors. Every section survives its source being
/// down ("unavailable: ...") - an observability outage must not stop the investigation.
/// </summary>
public sealed class ObservationCollector(IKubernetes kube, PrometheusClient prometheus, LokiClient loki, AlertmanagerClient alertmanager, IOptions<ToolsOptions> options)
    : IObservationCollector
{
    public async Task<string> CollectAsync(TaskSpec task, CancellationToken ct)
    {
        var ns = task.Namespace;
        options.Value.EnsureAllowed(ns);
        var sb = new StringBuilder();

        sb.AppendLine($"## Trigger ({task.Trigger.Kind}, {task.Trigger.At:HH:mm:ss}Z)");
        if (task.Trigger.Alerts.Count == 0) sb.AppendLine(task.Trigger.Note ?? "(no alerts: routine check)");
        foreach (var a in task.Trigger.Alerts)
            sb.AppendLine($"- [{a.Severity}] {a.Name} ns={a.Namespace} since {a.StartsAt:HH:mm:ss}Z: {a.Summary}");

        await Section(sb, "Alerts firing now", async () =>
        {
            var alerts = await alertmanager.ActiveAsync(ct);
            return alerts.Count == 0 ? "none" : string.Join('\n', alerts.Select(a => $"- [{a.Severity}] {a.Name} ns={a.Namespace} since {a.StartsAt:HH:mm:ss}Z"));
        });

        await Section(sb, $"Pods in {ns}", async () =>
        {
            var pods = await kube.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
            return string.Join('\n', pods.Items.OrderBy(p => p.Metadata.Name).Select(AgentTools.PodLine));
        });

        await Section(sb, "Warning events, last 15 min", async () =>
        {
            var since = DateTime.UtcNow.AddMinutes(-15);
            var events = await kube.CoreV1.ListNamespacedEventAsync(ns, cancellationToken: ct);
            var lines = events.Items.Where(e => e.Type == "Warning")
                .Select(e => (e, at: e.LastTimestamp ?? e.EventTime ?? e.Metadata.CreationTimestamp ?? DateTime.MinValue))
                .Where(x => x.at >= since).OrderByDescending(x => x.at).Take(10)
                .Select(x => $"- {x.at:HH:mm:ss}Z {x.e.Reason} {x.e.InvolvedObject.Kind}/{x.e.InvolvedObject.Name} (x{x.e.Count ?? 1}): {x.e.Message}");
            var text = string.Join('\n', lines);
            return text.Length == 0 ? "none" : text;
        });

        await Section(sb, "Share of bad requests per SLO (5m / 1h)", async () =>
        {
            var (fast, e1) = await prometheus.QueryAsync("slo:sli_error:ratio_rate5m", ct);
            var (slow, e2) = await prometheus.QueryAsync("slo:sli_error:ratio_rate1h", ct);
            if (e1 is not null || e2 is not null) return $"unavailable: {e1 ?? e2}";
            return string.Join('\n', fast.Select(s =>
            {
                var slo = s.Labels.GetValueOrDefault("slo", "?");
                var hour = slow.FirstOrDefault(x => x.Labels.GetValueOrDefault("slo") == slo)?.Value;
                return $"- {slo}: {s.Value:P1} (5m), {(hour is null ? "n/a" : hour.Value.ToString("P1"))} (1h); SLO allows 1%";
            }));
        });

        await Section(sb, "Request rate per service (5m)", async () =>
        {
            var (samples, error) = await prometheus.QueryAsync(
                $"sum by (app, http_response_status_code) (rate(http_server_request_duration_seconds_count{{namespace=\"{ns}\", http_route=~\".+\", http_route!~\"/health.*\"}}[5m]))", ct);
            return error is not null ? $"unavailable: {error}" : PrometheusClient.Format(samples, 15);
        });

        // Phase 9 game day: with +300 ms on the database the SLIs burned, but the snapshot only showed status codes
        // (all 200) - the model hunted for errors that didn't exist. A "bad request" can be a SLOW request.
        await Section(sb, "Latency per service, p50 / p99 (5m)", async () =>
        {
            var sel = $"namespace=\"{ns}\", http_route=~\".+\", http_route!~\"/health.*\"";
            var (p50, e1) = await prometheus.QueryAsync($"histogram_quantile(0.5, sum by (app, le) (rate(http_server_request_duration_seconds_bucket{{{sel}}}[5m])))", ct);
            var (p99, e2) = await prometheus.QueryAsync($"histogram_quantile(0.99, sum by (app, le) (rate(http_server_request_duration_seconds_bucket{{{sel}}}[5m])))", ct);
            var (db, e3) = await prometheus.QueryAsync($"histogram_quantile(0.99, sum by (app, le) (rate(db_client_operation_duration_seconds_bucket{{namespace=\"{ns}\"}}[5m])))", ct);
            if (e1 is not null || e2 is not null) return $"unavailable: {e1 ?? e2}";
            var lines = p99.Select(s =>
            {
                var app = s.Labels.GetValueOrDefault("app", "?");
                var median = p50.FirstOrDefault(x => x.Labels.GetValueOrDefault("app") == app)?.Value;
                return $"- {app}: p50 {Ms(median)}, p99 {Ms(s.Value)} (HTTP requests it serves)";
            }).ToList();
            if (e3 is null)
                lines.AddRange(db.Select(s => $"- {s.Labels.GetValueOrDefault("app", "?")} -> database: p99 {Ms(s.Value)} per query (as seen by the client)"));
            return lines.Count == 0 ? "no data" : string.Join('\n', lines);
        });

        await Section(sb, "Database", async () =>
        {
            var (samples, error) = await prometheus.QueryAsync($"pg_up{{namespace=\"{ns}\"}}", ct);
            if (error is not null) return $"unavailable: {error}";
            var text = samples.Count == 0 ? "pg_up: no series (exporter not running - is the postgres pod gone?)" : $"pg_up = {samples[0].Value}";
            try
            {
                var sts = await kube.AppsV1.ReadNamespacedStatefulSetAsync("postgres", ns, cancellationToken: ct);
                text += $"\nStatefulSet postgres: desired={sts.Spec.Replicas} ready={sts.Status.ReadyReplicas ?? 0}" +
                        (sts.Spec.Replicas == 0 ? " - scaled to ZERO in its spec (a configuration change, not a crash)" : "");
            }
            catch (k8s.Autorest.HttpOperationException) { }
            return text;
        });

        await Section(sb, "Warnings/errors in logs, last 10 min (newest first)", async () =>
        {
            var lines = await loki.QueryAsync($"{{namespace=\"{ns}\", level=~\"Warning|Error|Critical\"}}", TimeSpan.FromMinutes(10), 12, ct);
            return lines.Count == 0 ? "none" : string.Join('\n', lines.Select(l => $"- {l.At:HH:mm:ss}Z {l.Pod} [{l.Level}] {l.Message}"));
        });

        return sb.ToString().TrimEnd();
    }

    private static string Ms(double? seconds) => seconds is { } v && double.IsFinite(v) ? $"{v * 1000:0} ms" : "n/a";

    private static async Task Section(StringBuilder sb, string title, Func<Task<string>> body)
    {
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        try
        {
            sb.AppendLine(await body());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sb.AppendLine($"unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// F of RALF / the Ralph loop's stop condition: is the namespace in steady state RIGHT NOW?
///   1. none of the alerts that started the run is firing any more (alert runs; an incident has no trigger alerts),
///   2. every SLO's 5-minute share of bad requests is within its budget (1%), and
///   3. every pod is ready and no container restarted in the last 5 minutes (Phase 9: a memory leak keeps the SLIs
///      green while pods get OOMKilled one after another - that is not steady state).
/// Not "no alerts at all": burn-rate alerts lag on purpose - after a 5-minute outage the 6h/30m page can stay on for
/// ~30 minutes and the 1d/3d tickets for hours, long after the system recovered. Those are listed for the human.
/// Scheduled or manual check: one iteration is the whole task.
/// </summary>
public sealed class AlertsResolvedCompletionCheck(AlertmanagerClient alertmanager, PrometheusClient prometheus) : ICompletionCheck
{
    public const double ErrorBudget = 0.01;

    public async Task<CompletionStatus> CheckAsync(TaskSpec task, int iteration, CancellationToken ct)
    {
        if (task.Trigger.Kind is not (TriggerKind.Alert or TriggerKind.Incident))
            return new CompletionStatus(true, "one-off health check: a single iteration is the whole task");

        var active = (await alertmanager.ActiveAsync(ct)).Where(a => a.Namespace == task.Namespace).ToList();
        var triggers = task.Trigger.Alerts.Select(a => a.Name).ToHashSet();
        // Error-budget (burn-rate) alerts - label "slo" - describe the budget, not current health; current health is
        // the SLI check below. Only symptom alerts (PostgresDown, ...) keep the run open by themselves. Otherwise a
        // lagging 6h burn-rate page that fires after recovery would start a run that waits 30 minutes for nothing.
        var triggersFiring = active.Where(a => triggers.Contains(a.Name) && !a.IsSloAlert).Select(a => a.Name).Distinct().ToList();
        if (triggersFiring.Count > 0)
            return new CompletionStatus(false, $"still firing: {string.Join(", ", triggersFiring)}");

        var (slis, error) = await prometheus.QueryAsync($"slo:sli_error:ratio_rate5m{{namespace=\"{task.Namespace}\"}}", ct);
        if (error is not null)
            return new CompletionStatus(false, $"cannot read SLIs from Prometheus: {error}");
        var burning = slis.Where(s => s.Value > ErrorBudget)
            .Select(s => $"{s.Labels.GetValueOrDefault("slo", "?")} {s.Value:P1}")
            .ToList();
        if (burning.Count > 0)
            return new CompletionStatus(false, $"triggering alerts resolved, but bad requests over the last 5m exceed 1%: {string.Join(", ", burning)}");

        var (restarts, rError) = await prometheus.QueryAsync(
            $"sum by (pod) (increase(kube_pod_container_status_restarts_total{{namespace=\"{task.Namespace}\"}}[5m])) > 0", ct);
        if (rError is not null)
            return new CompletionStatus(false, $"cannot read container restarts from Prometheus: {rError}");
        if (restarts.Count > 0)
            return new CompletionStatus(false, $"SLIs within budget, but containers restarted in the last 5m: {string.Join(", ", restarts.Select(r => $"{r.Labels.GetValueOrDefault("pod", "?")} x{r.Value:0}"))}");
        var (notReady, nError) = await prometheus.QueryAsync(
            // Only live pods: a Completed/terminated pod is "not ready" too, and that's fine.
            $"sum by (pod) (kube_pod_status_ready{{namespace=\"{task.Namespace}\", condition=\"false\"}} * on (namespace, pod) group_left () " +
            $"(kube_pod_status_phase{{namespace=\"{task.Namespace}\", phase=~\"Running|Pending\"}} == 1)) > 0", ct);
        if (nError is not null)
            return new CompletionStatus(false, $"cannot read pod readiness from Prometheus: {nError}");
        if (notReady.Count > 0)
            return new CompletionStatus(false, $"SLIs within budget, but pods are not ready: {string.Join(", ", notReady.Select(r => r.Labels.GetValueOrDefault("pod", "?")))}");

        var lagging = active.Where(a => !triggers.Contains(a.Name)).Select(a => a.Name).Distinct().ToList();
        return new CompletionStatus(true,
            "steady state: triggering alerts resolved, every SLI within budget over the last 5m, all pods ready, no restarts" +
            (lagging.Count > 0 ? $"; still firing (lagging error-budget alerts, for a human): {string.Join(", ", lagging)}" : ""));
    }
}
