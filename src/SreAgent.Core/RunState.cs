using System.Text;
using System.Text.Json;

namespace SreAgent.Core;

/// <summary>
/// The Ralph loop's memory between iterations. Each iteration starts with a FRESH model context; this compact
/// summary is the only thing carried over (and it is persisted, so it survives an agent restart).
/// </summary>
public sealed class RunState
{
    public required string RunId { get; init; }
    public List<RunStateEntry> History { get; init; } = [];

    public void Append(IterationResult result) => History.Add(new RunStateEntry(
        result.Number,
        result.StartedAt,
        result.Analysis.Hypothesis,
        result.Analysis.Confidence,
        result.Analysis.Summary,
        result.Completion.Reason,
        result.Analysis.NextChecks));

    public string ToPromptText(int maxEntries = 5)
    {
        if (History.Count == 0) return "(first iteration: nothing is known yet)";
        var sb = new StringBuilder();
        foreach (var e in History.TakeLast(maxEntries))
        {
            sb.AppendLine($"- iteration {e.Iteration} at {e.At:HH:mm:ss}Z: hypothesis \"{e.Hypothesis}\" (confidence {e.Confidence}); task not done because: {e.CompletionReason}");
            if (e.NextChecks.Count > 0) sb.AppendLine($"  planned next checks: {string.Join("; ", e.NextChecks)}");
        }
        return sb.ToString().TrimEnd();
    }
}

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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
