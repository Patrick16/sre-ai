namespace SreAgent.Core;

public enum TriggerKind { Alert, Schedule, Manual }

public sealed record AlertInfo(
    string Name,
    string Severity,
    string? Namespace,
    string? Summary,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Labels,
    DateTimeOffset StartsAt);

public sealed record TriggerEvent(TriggerKind Kind, IReadOnlyList<AlertInfo> Alerts, string? Note, DateTimeOffset At);

public sealed record AgentLimits
{
    public int MaxIterations { get; init; } = 5;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxToolCallsPerIteration { get; init; } = 8;
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxToolOutputChars { get; init; } = 4000;

    /// <summary>A watcher must survive a model glitch: only this many failed iterations in a row end the run.</summary>
    public int MaxConsecutiveFailures { get; init; } = 2;
}

/// <summary>What the Ralph loop repeats until done: the same goal, every iteration, with a fresh LLM context.</summary>
public sealed record TaskSpec(string Id, string Goal, TriggerEvent Trigger, string Namespace, AgentLimits Limits, string Language);

/// <summary>The model's structured verdict for one iteration (parsed from its final JSON answer).</summary>
public sealed record Analysis(
    string Summary,
    string Hypothesis,
    string Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> NextChecks,
    bool Parsed,
    string RawText);

public sealed record CompletionStatus(bool Done, string Reason);

public sealed record ToolCallRecord(int Iteration, string Tool, string Arguments, string ResultPreview, bool Failed, TimeSpan Duration);

public sealed record IterationResult(
    int Number,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Model,
    string Observations,
    Analysis Analysis,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string Lead,
    CompletionStatus Completion,
    long InputTokens,
    long OutputTokens);

public enum RunOutcome { Resolved, LimitsExhausted, Failed, Cancelled }

public sealed class AgentRun(TaskSpec task, DateTimeOffset startedAt)
{
    public TaskSpec Task { get; } = task;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset? FinishedAt { get; set; }
    public RunOutcome? Outcome { get; set; }
    public string OutcomeReason { get; set; } = "";
    public List<IterationResult> Iterations { get; } = [];
    public string? ReportPath { get; set; }
}
