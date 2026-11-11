namespace SreAgent.Core;

// Phase 8: the agent may act - but only by PROPOSING a typed action; deterministic code decides whether it runs
// (docs/06). The model never gets a tool that changes anything.

/// <summary>Strictness order matters: the effective mode is the stricter of the global ceiling and the action's own.</summary>
public enum ActionMode { DryRun = 0, Approve = 1, Auto = 2 }

/// <summary>What the model asks for: a type from the catalog and ONE target name. No commands, no free-form arguments.</summary>
public sealed record ActionProposal(string Type, string Target, string Reason);

public enum ActionStatus
{
    /// <summary>A guardrail said no; nothing was sent to the cluster.</summary>
    Refused,
    /// <summary>Validated by the API server with dryRun=All; nothing changed.</summary>
    DryRun,
    Rejected,
    /// <summary>Nobody approved within the timeout - treated as "no".</summary>
    Expired,
    Executed,
    Failed,
}

public enum ActionEffect { NotMeasured, Improved, Unchanged, Degraded }

public sealed record GuardrailCheck(string Name, bool Passed, string Detail);

/// <summary>A cheap "how healthy is the namespace" reading, taken before an action and again after it.</summary>
public sealed record HealthSnapshot(DateTimeOffset At, double ErrorRatio, int NotReadyPods)
{
    public bool ErrorRatioMeasured => !double.IsNaN(ErrorRatio);

    public override string ToString() =>
        $"{(ErrorRatioMeasured ? ErrorRatio.ToString("P1") : "n/a")} bad requests (1m), {NotReadyPods} pod(s) not ready";
}

public sealed record ActionRecord(
    int Iteration,
    DateTimeOffset At,
    ActionProposal Proposal,
    ActionMode Mode,
    ActionStatus Status,
    string Detail,
    IReadOnlyList<GuardrailCheck> Checks,
    HealthSnapshot? Before = null,
    HealthSnapshot? After = null,
    ActionEffect Effect = ActionEffect.NotMeasured,
    string? ApprovedBy = null)
{
    public bool Executed => Status == ActionStatus.Executed;
    public string Key => ActionKey(Proposal.Type, Proposal.Target);
    public static string ActionKey(string type, string target) => $"{type}:{target}";
}

/// <summary>The actions that exist at all. Anything else the model proposes is refused by the allowlist.</summary>
public static class ActionCatalog
{
    public const string RestartDeployment = "restart_deployment";
    public const string DeletePod = "delete_pod";

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [RestartDeployment] = "rolling restart of a Deployment (pods replaced one by one, no downtime). Helps with stuck " +
                              "processes, stale connection pools, leaked memory. target = Deployment name, e.g. orders-api",
        [DeletePod] = "delete ONE pod so its Deployment recreates it. Only for Deployment pods, and only if other replicas " +
                      "stay ready. target = pod name",
    };
}

/// <summary>Configuration of the guardrails (bound from the "Actions" section by the host).</summary>
public sealed record ActionSettings
{
    /// <summary>Off switch in config; the kill switch (ConfigMap) is the runtime one.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Global ceiling: no action can be more autonomous than this. Default: describe only. Changed at runtime only
    /// through the agent's API (PUT /actions/mode); a restart goes back to the configured value.
    /// </summary>
    public ActionMode Mode { get; set; } = ActionMode.DryRun;

    /// <summary>The allowlist: action type -> its own mode. Types missing here don't exist for the agent.</summary>
    public IReadOnlyDictionary<string, ActionMode> Allowed { get; init; } = new Dictionary<string, ActionMode>();

    public int MaxPerRun { get; init; } = 2;
    public int MaxPerWindow { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Circuit breaker: after this many executed actions without improvement, stop acting for the run.</summary>
    public int MaxIneffective { get; init; } = 2;

    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Approve/Auto only: refuse when a fresh reading shows nothing wrong (the model's evidence can be stale).</summary>
    public bool RequireCurrentSymptom { get; init; } = true;

    /// <summary>How long to wait after an action before measuring its effect (rollout + metric windows).</summary>
    public TimeSpan VerifyDelay { get; init; } = TimeSpan.FromSeconds(90);

    /// <param name="runMode">the ceiling of one run (<see cref="TaskSpec.Mode"/>); null = the global ceiling.</param>
    public ActionMode EffectiveMode(string type, ActionMode? runMode = null) =>
        Allowed.TryGetValue(type, out var own) ? (ActionMode)Math.Min((int)own, (int)(runMode ?? Mode)) : ActionMode.DryRun;
}

/// <summary>Knows how to check and perform each catalog action against the cluster (implemented with the k8s API).</summary>
public interface IActionExecutor
{
    /// <summary>State-dependent preconditions (target exists, not protected, rollout not in progress...). null = OK.</summary>
    Task<string?> CheckPreconditionsAsync(ActionProposal proposal, string ns, CancellationToken ct);

    /// <summary>Performs the action; with <paramref name="dryRun"/> the API server validates it and changes nothing.</summary>
    Task<string> ExecuteAsync(ActionProposal proposal, string ns, bool dryRun, CancellationToken ct);
}

/// <summary>Runtime off switch a human can flip without redeploying the agent.</summary>
public interface IKillSwitch
{
    /// <returns>a reason when actions must not run; null when they may.</returns>
    Task<string?> EngagedAsync(CancellationToken ct);
}

public interface IHealthProbe
{
    Task<HealthSnapshot> SnapshotAsync(string ns, CancellationToken ct);
}

public sealed record ApprovalRequest(string Id, string RunId, int Iteration, string Namespace, ActionProposal Proposal, DateTimeOffset ExpiresAt);

public sealed record ApprovalDecision(bool Approved, string By, bool Expired = false);

/// <summary>Human in the loop: publishes a proposal and waits for a decision (or the timeout).</summary>
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Executed actions across ALL runs - for the global rate limit and the per-target cooldown. A new run must not reset
/// them (an alert storm would otherwise mean a restart per alert), so the ledger is process-wide and persisted.
/// </summary>
public interface IActionLedger
{
    IReadOnlyList<(DateTimeOffset At, string Key)> Since(DateTimeOffset from);
    Task RecordAsync(DateTimeOffset at, string key, CancellationToken ct);
}

public sealed class InMemoryActionLedger : IActionLedger
{
    private readonly List<(DateTimeOffset At, string Key)> _entries = [];

    public IReadOnlyList<(DateTimeOffset At, string Key)> Since(DateTimeOffset from)
    {
        lock (_entries) return _entries.Where(e => e.At >= from).ToList();
    }

    public Task RecordAsync(DateTimeOffset at, string key, CancellationToken ct)
    {
        lock (_entries) _entries.Add((at, key));
        return Task.CompletedTask;
    }
}

public static class EffectJudge
{
    /// <summary>The SLO budget: at or below this share of bad requests the namespace counts as healthy.</summary>
    public const double HealthyErrorRatio = 0.01;

    /// <summary>
    /// Compares before/after. Deliberately coarse: "degraded" must be unmistakable (it stops the run), "improved" must
    /// be clear (it justifies the action), everything in between is "unchanged".
    /// </summary>
    public static ActionEffect Judge(HealthSnapshot before, HealthSnapshot after)
    {
        // Pods were measured, the error ratio was not (before or after): only the pod count can tell, and if it did not
        // change either, we know nothing - "NotMeasured", not "Unchanged" (which would count against the circuit breaker).
        if ((!before.ErrorRatioMeasured || !after.ErrorRatioMeasured) && before.NotReadyPods == after.NotReadyPods)
            return ActionEffect.NotMeasured;
        if (after.NotReadyPods > before.NotReadyPods || after.ErrorRatio > before.ErrorRatio + 0.05)
            return ActionEffect.Degraded;
        var errorsFixed = before.ErrorRatio > HealthyErrorRatio && (after.ErrorRatio <= HealthyErrorRatio || after.ErrorRatio < before.ErrorRatio * 0.5);
        if (errorsFixed || after.NotReadyPods < before.NotReadyPods) return ActionEffect.Improved;
        return ActionEffect.Unchanged;
    }
}
