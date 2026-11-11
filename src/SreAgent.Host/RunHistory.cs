using System.Text.Json;

namespace SreAgent.Host;

/// <summary>
/// The list of runs and their iterations on disk ({DataDir}/runs), so the console still shows yesterday's runs - who
/// started them, with which model and mode, and how the agent reasoned - after the agent restarted.
///   {id}.json        the run's summary (written when it is queued and again when it finishes)
///   {id}.trace.json  its iterations as the console shows them (rewritten after every iteration)
/// Best effort: a failed write is logged and never stops a run. Old files are pruned at startup
/// (<c>Agent:HistoryRetentionDays</c>); the Markdown reports are a separate, permanent record.
/// </summary>
public sealed class RunHistory(string directory, ILogger<RunHistory> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string TraceSuffix = ".trace.json";

    public void SaveSummary(RunSummary run) => Write(Path.Combine(directory, $"{run.Id}.json"), run);

    public void SaveTrace(string runId, IReadOnlyList<IterationView> iterations) => Write(Path.Combine(directory, runId + TraceSuffix), iterations);

    public IReadOnlyList<RunSummary> LoadSummaries()
    {
        if (!Directory.Exists(directory)) return [];
        var runs = new List<RunSummary>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Where(f => !f.EndsWith(TraceSuffix, StringComparison.Ordinal)))
            if (Read<RunSummary>(file) is { Id.Length: > 0 } run) runs.Add(run);
        return runs;
    }

    public IReadOnlyList<IterationView> LoadTrace(string runId)
    {
        // The id comes from a URL: only a plain file name may reach the file system.
        if (runId.Length == 0 || runId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return [];
        var path = Path.Combine(directory, runId + TraceSuffix);
        return File.Exists(path) ? Read<List<IterationView>>(path) ?? [] : [];
    }

    private void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, overwrite: true); // atomic replace: never a half-written file
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not write run history file {Path}", path);
        }
    }

    private T? Read<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read run history file {Path}", path);
            return null;
        }
    }
}
