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
    string RawText)
{
    /// <summary>Facts the model verified in this iteration, each with its evidence. Carried over in RunState.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];

    /// <summary>At most one action the model proposes; guardrails decide whether anything happens.</summary>
    public ActionProposal? Action { get; init; }
}

/// <summary>
/// The run-level conclusion, written once after the loop over ALL iterations: the last iteration alone is the worst
/// judge of the cause - after recovery it only sees the aftermath.
/// </summary>
public sealed record RunSynthesis(
    string Summary,
    string RootCause,
    string Confidence,
    IReadOnlyList<string> Timeline,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Recommendations,
    string Disagreements,
    bool Parsed);

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
    long OutputTokens)
{
    public ActionRecord? Action { get; init; }
}

/// <summary>Escalated: the agent stopped on purpose (its action made things worse) - a human must take over.</summary>
public enum RunOutcome { Resolved, LimitsExhausted, Escalated, Failed, Cancelled }

public sealed class AgentRun(TaskSpec task, DateTimeOffset startedAt)
{
    public TaskSpec Task { get; } = task;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public DateTimeOffset? FinishedAt { get; set; }
    public RunOutcome? Outcome { get; set; }
    public string OutcomeReason { get; set; } = "";
    public List<IterationResult> Iterations { get; } = [];
    public IReadOnlyList<string> Facts { get; set; } = [];
    public IReadOnlyList<ActionRecord> Actions => Iterations.Where(i => i.Action is not null).Select(i => i.Action!).ToList();
    public RunSynthesis? Synthesis { get; set; }
    public string? ReportPath { get; set; }
}
