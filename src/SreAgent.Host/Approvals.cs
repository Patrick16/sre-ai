using System.Collections.Concurrent;
using System.Text.Json;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>
/// Approve mode: the proposal waits here until a human decides via the API, or the timeout passes (= no).
///   GET  /approvals                 pending proposals
///   POST /approvals/{id}  {"approve": true, "by": "alice"}
/// Who may decide = who can reach the agent's API AND knows <c>Agent:ApiToken</c> (header X-Agent-Token; when the
/// token is empty only the NetworkPolicy protects the API: Alertmanager's pod and `kubectl port-forward`).
/// "by" is a free-text name for the audit log, not an identity. A real setup would add real authentication or a chat integration.
/// </summary>
public sealed class ApprovalGate(TimeProvider time) : IApprovalGate
{
    private readonly ConcurrentDictionary<string, (ApprovalRequest Request, TaskCompletionSource<ApprovalDecision> Decision)> _pending = new();

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = (request, tcs);
        try
        {
            var timeout = request.ExpiresAt - time.GetUtcNow();
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero, time, ct));
            return finished == tcs.Task ? await tcs.Task : new ApprovalDecision(false, "timeout", Expired: true);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public IEnumerable<ApprovalRequest> Pending() => _pending.Values.Select(p => p.Request).OrderBy(r => r.ExpiresAt);

    public bool Decide(string id, bool approve, string by) =>
        _pending.TryGetValue(id, out var p) && p.Decision.TrySetResult(new ApprovalDecision(approve, by));
}

/// <summary>Executed actions across runs and agent restarts: {DataDir}/actions-ledger.json (last 24 h).</summary>
public sealed class FileActionLedger : IActionLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly List<Entry> _entries;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileActionLedger(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _entries = File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    public IReadOnlyList<(DateTimeOffset At, string Key)> Since(DateTimeOffset from)
    {
        lock (_entries) return _entries.Where(e => e.At >= from).Select(e => (e.At, e.Key)).ToList();
    }

    public async Task RecordAsync(DateTimeOffset at, string key, CancellationToken ct)
    {
        string json;
        lock (_entries)
        {
            _entries.Add(new Entry(at, key));
            _entries.RemoveAll(e => e.At < _time.GetUtcNow().AddDays(-1));
            json = JsonSerializer.Serialize(_entries, Json);
        }
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path + ".tmp", json, ct);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record Entry(DateTimeOffset At, string Key);
}

/// <summary>"Actions" section of the configuration (env Actions__*), turned into the guardrails' settings.</summary>
public sealed class ActionsOptions
{
    public const string Section = "Actions";

    public bool Enabled { get; set; } = true;

    public ActionMode Mode { get; set; } = ActionMode.DryRun;

    /// <summary>Action type -> its own mode, e.g. Actions__Allowed__restart_deployment=Auto. Empty = observe-only.</summary>
    public Dictionary<string, ActionMode> Allowed { get; set; } = [];

    public int MaxPerRun { get; set; } = 2;
    public int MaxPerWindow { get; set; } = 3;
    public int WindowMinutes { get; set; } = 30;
    public int CooldownMinutes { get; set; } = 10;
    public int MaxIneffective { get; set; } = 2;
    public int ApprovalTimeoutMinutes { get; set; } = 5;
    public int VerifyDelaySeconds { get; set; } = 90;
    public bool RequireCurrentSymptom { get; set; } = true;

    public ActionSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        Allowed = Allowed,
        MaxPerRun = MaxPerRun,
        MaxPerWindow = MaxPerWindow,
        Window = TimeSpan.FromMinutes(WindowMinutes),
        Cooldown = TimeSpan.FromMinutes(CooldownMinutes),
        MaxIneffective = MaxIneffective,
        ApprovalTimeout = TimeSpan.FromMinutes(ApprovalTimeoutMinutes),
        VerifyDelay = TimeSpan.FromSeconds(VerifyDelaySeconds),
        RequireCurrentSymptom = RequireCurrentSymptom,
    };
}

/// <summary>The shared secret of the endpoints that change something. Compared in constant time.</summary>
public static class ApiToken
{
    public const string Header = "X-Agent-Token";

    /// <summary>An empty configured token means "not required".</summary>
    public static bool IsValid(string configured, string? supplied)
    {
        if (string.IsNullOrEmpty(configured)) return true;
        if (string.IsNullOrEmpty(supplied)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(configured), System.Text.Encoding.UTF8.GetBytes(supplied));
    }
}

/// <summary>Deletes old files from a directory (used for RunState files, which are only diagnostics).</summary>
public static class DataRetention
{
    /// <returns>how many files were deleted; retention &lt;= 0 keeps everything.</returns>
    public static int PruneOldFiles(string directory, TimeSpan retention, DateTime utcNow)
    {
        if (retention <= TimeSpan.Zero || !Directory.Exists(directory)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (utcNow - File.GetLastWriteTimeUtc(file) <= retention) continue;
                File.Delete(file);
                removed++;
            }
            catch (IOException) { } // in use or already gone: next startup
        }
        return removed;
    }
}
