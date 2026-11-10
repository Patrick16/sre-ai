using System.Text.RegularExpressions;

namespace SreAgent.Core;

/// <summary>
/// Layers 1-4 of docs/06, in code, in a fixed order; the first failing check refuses the action. Each check is
/// recorded (passed or not) so the report shows WHY an action ran or didn't. Layer 5 (RBAC + admission policy in
/// the cluster) doesn't trust this class: it would stop a bug here too.
/// </summary>
public sealed partial class Guardrails(ActionSettings settings, IActionExecutor executor, IKillSwitch killSwitch, IActionLedger ledger, IHealthProbe probe, TimeProvider time)
{
    public ActionSettings Settings => settings;

    public async Task<(bool Allowed, ActionMode Mode, IReadOnlyList<GuardrailCheck> Checks)> EvaluateAsync(
        ActionProposal proposal, TaskSpec task, RunState state, CancellationToken ct)
    {
        var checks = new List<GuardrailCheck>();
        var mode = settings.EffectiveMode(proposal.Type);

        bool Check(string name, bool passed, string detail)
        {
            checks.Add(new GuardrailCheck(name, passed, detail));
            return passed;
        }

        // 1. Allowlist: does this action exist for the agent at all?
        if (!Check("enabled", settings.Enabled, settings.Enabled ? "actions enabled in config" : "Actions:Enabled=false")) return (false, mode, checks);
        if (!Check("allowlist", settings.Allowed.ContainsKey(proposal.Type) && ActionCatalog.Descriptions.ContainsKey(proposal.Type),
                $"type '{proposal.Type}'; allowed: {string.Join(", ", settings.Allowed.Keys)}")) return (false, mode, checks);

        // 2. Scope: a single, well-formed object name in the run's namespace. The target comes from the model, i.e.
        // possibly from injected text in a log line - so it is validated like any untrusted input.
        var validTarget = proposal.Target.Length <= 253 && DnsName().IsMatch(proposal.Target);
        if (!Check("target", validTarget, validTarget ? $"'{proposal.Target}' is one object name" : $"'{proposal.Target}' is not ONE Kubernetes object name")) return (false, mode, checks);

        // 3. Runtime kill switch - checked on every action, never cached.
        var killed = await killSwitch.EngagedAsync(ct);
        if (!Check("kill-switch", killed is null, killed ?? "not engaged")) return (false, mode, checks);

        // 4. Limits within this run: count, circuit breaker, no repeating what didn't help.
        var executed = state.Actions.Where(a => a.Executed).ToList();
        if (!Check("run-limit", executed.Count < settings.MaxPerRun, $"{executed.Count} of {settings.MaxPerRun} actions used in this run")) return (false, mode, checks);
        var ineffective = executed.Count(a => a.Effect is ActionEffect.Unchanged or ActionEffect.Degraded);
        var breakerOpen = ineffective >= settings.MaxIneffective;
        if (!Check("circuit-breaker", !breakerOpen, breakerOpen
                ? $"{ineffective} executed action(s) without improvement (limit {settings.MaxIneffective}) - acting has stopped helping, a human should look"
                : $"{ineffective} executed action(s) without improvement (limit {settings.MaxIneffective})"))
            return (false, mode, checks);
        var key = ActionRecord.ActionKey(proposal.Type, proposal.Target);
        var useless = executed.FirstOrDefault(a => a.Key == key && a.Effect != ActionEffect.Improved);
        if (!Check("no-repeat", useless is null,
                useless is null ? "not tried in this run" : $"already done in iteration {useless.Iteration} with effect {useless.Effect}"))
            return (false, mode, checks);

        // 5. Limits across runs: global rate and per-target cooldown.
        var now = time.GetUtcNow();
        var inWindow = ledger.Since(now - settings.Window);
        if (!Check("rate-limit", inWindow.Count < settings.MaxPerWindow, $"{inWindow.Count} of {settings.MaxPerWindow} actions in the last {settings.Window.TotalMinutes:0} min"))
            return (false, mode, checks);
        var last = inWindow.Where(e => e.Key == key).Select(e => (DateTimeOffset?)e.At).Max();
        var coolingDown = last is { } at && now - at < settings.Cooldown;
        if (!Check("cooldown", !coolingDown, coolingDown ? $"same action on the same target at {last:HH:mm:ss}Z, cooldown {settings.Cooldown.TotalMinutes:0} min" : "no recent action on this target"))
            return (false, mode, checks);

        // 6. Is there a symptom RIGHT NOW? The model reasons over lagging data - 10 minutes of logs, a 5-minute SLI - and
        // in a real run proposed a restart for "stale connections" a minute after the errors had already stopped by
        // themselves. A fresh 1-minute reading decides; DryRun still goes through (it changes nothing and teaches).
        if (settings.RequireCurrentSymptom && mode != ActionMode.DryRun)
        {
            var now1m = await probe.SnapshotAsync(task.Namespace, ct);
            var symptom = now1m.ErrorRatio > EffectJudge.HealthyErrorRatio || now1m.NotReadyPods > 0;
            if (!Check("current-symptom", symptom, symptom ? $"now: {now1m}" : $"nothing to fix right now ({now1m}) - the evidence is older than the present"))
                return (false, mode, checks);
        }

        // 7. Preconditions on the live object (exists, not protected, safe to touch right now).
        var refusal = await executor.CheckPreconditionsAsync(proposal, task.Namespace, ct);
        if (!Check("preconditions", refusal is null, refusal ?? "target checked")) return (false, mode, checks);

        return (true, mode, checks);
    }

    public Task RecordExecutedAsync(DateTimeOffset at, string key, CancellationToken ct) => ledger.RecordAsync(at, key, ct);

    // RFC 1123 subdomain: what a Deployment/pod name can be. No commas, slashes, spaces or wildcards.
    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$")]
    private static partial Regex DnsName();
}
