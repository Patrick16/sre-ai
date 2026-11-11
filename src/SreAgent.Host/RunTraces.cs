using System.Collections.Concurrent;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>
/// Keeps the iterations of the last runs in memory, so the console can show a run while it is going
/// (GET /runs/{id}/trace). Not persisted: after an agent restart the Markdown report stays the record.
/// </summary>
public sealed class RunTraceStore : IRunObserver
{
    private const int MaxRuns = 30;
    private readonly ConcurrentDictionary<string, List<IterationResult>> _iterations = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly object _lock = new();

    public void IterationFinished(TaskSpec task, IterationResult result)
    {
        lock (_lock)
        {
            if (!_iterations.ContainsKey(task.Id))
            {
                _iterations[task.Id] = [];
                _order.Enqueue(task.Id);
                while (_order.Count > MaxRuns && _order.TryDequeue(out var old)) _iterations.TryRemove(old, out _);
            }
            _iterations[task.Id].Add(result);
        }
    }

    /// <summary>The iterations finished so far, in order. Empty for a run this process did not see.</summary>
    public IReadOnlyList<IterationView> Get(string runId)
    {
        lock (_lock)
        {
            return _iterations.TryGetValue(runId, out var list) ? list.Select(IterationView.From).ToList() : [];
        }
    }
}

/// <summary>
/// One iteration as the console shows it: the model's answer, its reasoning text (RawText, whatever the model
/// wrote), the tool calls, what the code observed, and what the Lead did with the proposal.
/// </summary>
public sealed record IterationView(
    int Number,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    string Model,
    string Summary,
    string Hypothesis,
    string Confidence,
    bool Parsed,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> NextChecks,
    IReadOnlyList<string> Facts,
    string Thinking,
    string Observations,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string Lead,
    bool Done,
    string CompletionReason,
    long InputTokens,
    long OutputTokens,
    ActionView? Action)
{
    public static IterationView From(IterationResult r) => new(
        r.Number,
        r.StartedAt,
        r.Duration.TotalSeconds,
        r.Model,
        r.Analysis.Summary,
        r.Analysis.Hypothesis,
        r.Analysis.Confidence,
        r.Analysis.Parsed,
        r.Analysis.Evidence,
        r.Analysis.Recommendations,
        r.Analysis.NextChecks,
        r.Analysis.Facts,
        r.Analysis.RawText,
        r.Observations,
        r.ToolCalls,
        r.Lead,
        r.Completion.Done,
        r.Completion.Reason,
        r.InputTokens,
        r.OutputTokens,
        r.Action is null ? null : ActionView.From(r.Action));
}

/// <summary>An action as the console shows it: enums as names (the default JSON would print numbers).</summary>
public sealed record ActionView(
    int Iteration,
    DateTimeOffset At,
    string Type,
    string Target,
    string Reason,
    string Mode,
    string Status,
    string Detail,
    IReadOnlyList<GuardrailCheck> Checks,
    string? Before,
    string? After,
    string Effect,
    string? ApprovedBy)
{
    public static ActionView From(ActionRecord a) => new(
        a.Iteration, a.At, a.Proposal.Type, a.Proposal.Target, a.Proposal.Reason,
        a.Mode.ToString(), a.Status.ToString(), a.Detail, a.Checks, a.Before?.ToString(), a.After?.ToString(),
        a.Effect.ToString(), a.ApprovedBy);
}
