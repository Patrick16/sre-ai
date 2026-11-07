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
///   1. none of the alerts that started the run is firing any more, and
///   2. every SLO's 5-minute share of bad requests is within its budget (1%).
/// Not "no alerts at all": burn-rate alerts lag on purpose - after a 5-minute outage the 6h/30m page can stay on for
/// ~30 minutes and the 1d/3d tickets for hours, long after the system recovered. Those are listed for the human.
/// Scheduled or manual check: one iteration is the whole task.
/// </summary>
public sealed class AlertsResolvedCompletionCheck(AlertmanagerClient alertmanager, PrometheusClient prometheus) : ICompletionCheck
{
    public const double ErrorBudget = 0.01;

    public async Task<CompletionStatus> CheckAsync(TaskSpec task, int iteration, CancellationToken ct)
    {
        if (task.Trigger.Kind != TriggerKind.Alert)
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

        var lagging = active.Where(a => !triggers.Contains(a.Name)).Select(a => a.Name).Distinct().ToList();
        return new CompletionStatus(true,
            "steady state: triggering alerts resolved and every SLI is within budget over the last 5m" +
            (lagging.Count > 0 ? $"; still firing (lagging error-budget alerts, for a human): {string.Join(", ", lagging)}" : ""));
    }
}
