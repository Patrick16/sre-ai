using SreAgent.Core;

namespace SreAgent.Core.Tests;

internal sealed class FakeExecutor(string? precondition = null) : IActionExecutor
{
    public string? Precondition { get; set; } = precondition;
    public List<(ActionProposal Proposal, bool DryRun)> Calls { get; } = [];
    public Exception? Throw { get; set; }
    public Exception? ThrowOnPrecondition { get; set; }

    public Task<string?> CheckPreconditionsAsync(ActionProposal proposal, string ns, CancellationToken ct) =>
        ThrowOnPrecondition is not null ? throw ThrowOnPrecondition : Task.FromResult(Precondition);

    public Task<string> ExecuteAsync(ActionProposal proposal, string ns, bool dryRun, CancellationToken ct)
    {
        Calls.Add((proposal, dryRun));
        if (Throw is not null) throw Throw;
        return Task.FromResult(dryRun ? $"would {proposal.Type} {proposal.Target}" : $"did {proposal.Type} {proposal.Target}");
    }
}

internal sealed class FakeKillSwitch(string? reason = null) : IKillSwitch
{
    public string? Reason { get; set; } = reason;
    public Task<string?> EngagedAsync(CancellationToken ct) => Task.FromResult(Reason);
}

/// <summary>Returns the given snapshots in order (before, after, before, after...).</summary>
internal sealed class FakeProbe(params (double ErrorRatio, int NotReady)[] readings) : IHealthProbe
{
    private readonly Queue<(double, int)> _readings = new(readings);
    public Task<HealthSnapshot> SnapshotAsync(string ns, CancellationToken ct)
    {
        var (e, n) = _readings.Count > 0 ? _readings.Dequeue() : (0, 0);
        return Task.FromResult(new HealthSnapshot(DateTimeOffset.UtcNow, e, n));
    }
}

/// <summary>First reading fine, every later one fails: e.g. Prometheus goes away right after the action ran.</summary>
internal sealed class ProbeThatFailsAfterFirst(double errorRatio = 0.5) : IHealthProbe
{
    private int _calls;
    public Task<HealthSnapshot> SnapshotAsync(string ns, CancellationToken ct) =>
        Interlocked.Increment(ref _calls) == 1
            ? Task.FromResult(new HealthSnapshot(DateTimeOffset.UtcNow, errorRatio, 0))
            : throw new HttpRequestException("prometheus is gone");
}

/// <summary>Always the same reading - e.g. "there is a symptom right now" for the guardrails' current-symptom check.</summary>
internal sealed class ConstantProbe(double errorRatio = 0.5, int notReady = 0) : IHealthProbe
{
    public Task<HealthSnapshot> SnapshotAsync(string ns, CancellationToken ct) => Task.FromResult(new HealthSnapshot(DateTimeOffset.UtcNow, errorRatio, notReady));
}

internal sealed class FakeApprovals(ApprovalDecision decision) : IApprovalGate
{
    public List<ApprovalRequest> Requests { get; } = [];
    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(decision);
    }
}

internal static class ActionTest
{
    public static ActionSettings Settings(ActionMode mode = ActionMode.Auto, ActionMode own = ActionMode.Auto) => new()
    {
        Mode = mode,
        Allowed = new Dictionary<string, ActionMode> { [ActionCatalog.RestartDeployment] = own, [ActionCatalog.DeletePod] = ActionMode.Approve },
        VerifyDelay = TimeSpan.Zero,
    };

    public static string JsonWithAction(string hypothesis, string type, string target) =>
        $$"""{"summary":"s","hypothesis":"{{hypothesis}}","confidence":"high","evidence":["e"],"facts":[],"recommendations":["r"],"next_checks":[],"action":{"type":"{{type}}","target":"{{target}}","reason":"stale pool"} }""";
}
