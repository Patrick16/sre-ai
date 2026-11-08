using System.Collections.Concurrent;
using System.Text.Json;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>
/// Approve mode: the proposal waits here until a human decides via the API, or the timeout passes (= no).
///   GET  /approvals                 pending proposals
///   POST /approvals/{id}  {"approve": true, "by": "alice"}
/// Who may decide = who can reach the agent's API: in the cluster that's only via `kubectl port-forward`, i.e.
/// people with access to the sre-agent namespace. A real setup would add authentication / a chat integration.
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
