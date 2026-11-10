using System.Text;
using System.Text.Json;

namespace SreAgent.Core;

/// <summary>
/// The Ralph loop's memory between iterations. Each iteration starts with a FRESH model context; this compact
/// summary is the only thing carried over (and it is persisted, so it survives an agent restart).
/// </summary>
public sealed class RunState
{
    // Per iteration, not "drop the oldest": the oldest facts were verified WHILE the incident was active - the most
    // valuable ones. 10 iterations x 5 = 50 short lines, a few thousand tokens.
    public const int MaxFactsPerIteration = 5;
    public const int MaxFacts = 50;

    public required string RunId { get; init; }
    public List<RunStateEntry> History { get; init; } = [];

    /// <summary>
    /// Verified facts, kept apart from hypotheses. A hypothesis is an opinion the next fresh context may overturn;
    /// a fact ("at 21:53 the StatefulSet had desired=0", with its evidence) is what it was at that time - it stays
    /// true about the past even when the current state has changed, so it must not get lost between iterations.
    /// </summary>
    public List<FactEntry> Facts { get; init; } = [];

    /// <summary>Every proposed action and what became of it: the guardrails' memory (limits, no-repeat, circuit breaker).</summary>
    public List<ActionRecord> Actions { get; init; } = [];

    public void Append(IterationResult result)
    {
        if (result.Action is { } action) Actions.Add(action);

        History.Add(new RunStateEntry(
            result.Number,
            result.StartedAt,
            result.Analysis.Hypothesis,
            result.Analysis.Confidence,
            result.Analysis.Summary,
            result.Completion.Reason,
            result.Analysis.NextChecks));

        foreach (var fact in result.Analysis.Facts.Take(MaxFactsPerIteration))
        {
            if (Facts.Count >= MaxFacts) break;
            var normalized = fact.Trim();
            if (normalized.Length == 0 || Facts.Any(f => string.Equals(f.Text, normalized, StringComparison.OrdinalIgnoreCase))) continue;
            Facts.Add(new FactEntry(result.Number, result.StartedAt, normalized));
        }
    }

    public string ToPromptText(int maxEntries = 5)
    {
        if (History.Count == 0) return "(first iteration: nothing is known yet)";
        var sb = new StringBuilder();
        if (Facts.Count > 0)
        {
            sb.AppendLine("Established facts (verified at the time shown; they remain true about that moment even if the current state differs):");
            foreach (var f in Facts) sb.AppendLine($"- [iteration {f.Iteration}, {f.At:HH:mm:ss}Z] {f.Text}");
            sb.AppendLine();
        }
        if (Actions.Count > 0)
        {
            sb.AppendLine("Actions proposed earlier in this run (do not propose again what was refused or did not help):");
            foreach (var a in Actions)
                sb.AppendLine($"- iteration {a.Iteration}: {a.Proposal.Type} {a.Proposal.Target} -> {a.Status}" +
                              (a.Executed ? $", effect {a.Effect} (before: {a.Before}; after: {a.After})" : "") + $": {a.Detail}");
            sb.AppendLine();
        }
        sb.AppendLine("Earlier hypotheses (opinions - confirm or refute them with evidence):");
        foreach (var e in History.TakeLast(maxEntries))
        {
            sb.AppendLine($"- iteration {e.Iteration} at {e.At:HH:mm:ss}Z: \"{e.Hypothesis}\" (confidence {e.Confidence}); task not done because: {e.CompletionReason}");
            if (e.NextChecks.Count > 0) sb.AppendLine($"  planned next checks: {string.Join("; ", e.NextChecks)}");
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record FactEntry(int Iteration, DateTimeOffset At, string Text);

public sealed record RunStateEntry(
    int Iteration,
    DateTimeOffset At,
    string Hypothesis,
    string Confidence,
    string Summary,
    string CompletionReason,
    IReadOnlyList<string> NextChecks);

public sealed class FileRunStateStore(string directory) : IRunStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<RunState?> LoadAsync(string runId, CancellationToken ct)
    {
        var path = PathFor(runId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RunState>(stream, Json, ct);
    }

    public async Task SaveAsync(RunState state, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var tmp = PathFor(state.RunId) + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state, Json), ct);
        File.Move(tmp, PathFor(state.RunId), overwrite: true); // atomic replace: never a half-written state file
    }

    private string PathFor(string runId) => Path.Combine(directory, $"{runId}.json");
}
