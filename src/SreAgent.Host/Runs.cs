using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SreAgent.Core;

namespace SreAgent.Host;

public sealed record RunRequest(TaskSpec Task, string? Model);

public sealed record RunSummary(
    string Id,
    string Trigger,
    string Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? FinishedAt,
    int Iterations,
    string? Hypothesis,
    string? Outcome,
    string? OutcomeReason,
    string? ReportPath);

/// <summary>
/// Queue + bookkeeping. One run per namespace at a time: a database outage fires PostgresDown and, minutes later,
/// two burn-rate alerts - that is one problem, so later alerts don't start parallel investigations; the running
/// one sees them in its next Retrieve anyway. One worker overall: a single local model can't serve two runs well.
/// </summary>
public sealed class RunQueue(IOptions<AgentOptions> options, TimeProvider time)
{
    // FullMode.Wait: TryWrite then returns false when full. (DropWrite would return true and silently lose the run.)
    private readonly Channel<RunRequest> _channel = Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, RunSummary> _runs = new();
    private readonly ConcurrentDictionary<string, string> _activeByNamespace = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _handled = new();
    private const int MaxRemembered = 200;

    public ChannelReader<RunRequest> Reader => _channel.Reader;

    public TaskSpec NewTask(TriggerEvent trigger, string goal, string? ns = null)
    {
        var o = options.Value;
        var id = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{trigger.Kind.ToString().ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";
        return new TaskSpec(id, goal, trigger, ns ?? o.Namespace, o.Limits.ToLimits(), o.Language);
    }

    /// <returns>false when a run for the same namespace is already queued or running, or the queue is full.</returns>
    public bool TryEnqueue(RunRequest request)
    {
        var ns = request.Task.Namespace;
        var id = request.Task.Id;
        if (!_activeByNamespace.TryAdd(ns, id)) return false;

        // Bookkeeping BEFORE the channel write: the worker may take the request off the queue immediately, and
        // MarkRunning must already find it (the other order was a race that could crash the worker).
        _runs[id] = new RunSummary(id, request.Task.Trigger.Kind.ToString(), "queued", time.GetUtcNow(), null, 0, null, null, null, null);
        if (!_channel.Writer.TryWrite(request))
        {
            _runs.TryRemove(id, out _);
            _activeByNamespace.TryRemove(ns, out _);
            return false;
        }
        Trim();
        return true;
    }

    public void MarkRunning(string id)
    {
        if (_runs.TryGetValue(id, out var s)) _runs[id] = s with { Status = "running" };
    }

    public void Complete(AgentRun run)
    {
        _activeByNamespace.TryRemove(run.Task.Namespace, out _);
        var id = run.Task.Id;
        var queuedAt = _runs.TryGetValue(id, out var old) ? old.QueuedAt : run.StartedAt;
        _runs[id] = new RunSummary(id, run.Task.Trigger.Kind.ToString(), "finished", queuedAt, run.FinishedAt, run.Iterations.Count,
            run.Synthesis?.RootCause ?? run.Iterations.LastOrDefault()?.Analysis.Hypothesis,
            run.Outcome?.ToString(), run.OutcomeReason, run.ReportPath);
    }

    /// <summary>The agent is shutting down mid-run: free the namespace slot (the run is not coming back).</summary>
    public void Abandon(TaskSpec task) => _activeByNamespace.TryRemove(task.Namespace, out _);

    /// <summary>The list is in memory only; keep the newest <see cref="MaxRemembered"/> finished runs (reports stay on disk).</summary>
    private void Trim()
    {
        if (_runs.Count <= MaxRemembered) return;
        foreach (var r in _runs.Values.Where(r => r.Status == "finished").OrderBy(r => r.QueuedAt).Take(_runs.Count - MaxRemembered))
            _runs.TryRemove(r.Id, out _);
    }

    // --- Alert de-duplication -------------------------------------------------------------------------------------
    // Alertmanager repeats a firing alert every repeat_interval (1h). Without this, an alert that stays on for hours
    // would start a new LLM run - minutes of GPU - every hour for the same problem.

    /// <summary>True when every one of these alerts (by fingerprint) already started a run within the suppression window.</summary>
    public bool AlreadyHandled(IEnumerable<string> fingerprints)
    {
        var window = TimeSpan.FromMinutes(options.Value.AlertSuppressionMinutes);
        if (window <= TimeSpan.Zero) return false;
        var now = time.GetUtcNow();
        var list = fingerprints.ToList();
        return list.Count > 0 && list.All(f => _handled.TryGetValue(f, out var at) && now - at < window);
    }

    public void RememberHandled(IEnumerable<string> fingerprints)
    {
        var now = time.GetUtcNow();
        foreach (var f in fingerprints) _handled[f] = now;
        foreach (var old in _handled.Where(kv => now - kv.Value > TimeSpan.FromHours(24)).Select(kv => kv.Key)) _handled.TryRemove(old, out _);
    }

    /// <summary>A resolved alert that fires again is a NEW problem and must be investigated again.</summary>
    public void ForgetHandled(string fingerprint) => _handled.TryRemove(fingerprint, out _);

    public string? ActiveRunFor(string ns) => _activeByNamespace.GetValueOrDefault(ns);

    public IEnumerable<RunSummary> List() => _runs.Values.OrderByDescending(r => r.QueuedAt).Take(50);

    public RunSummary? Get(string id) => _runs.GetValueOrDefault(id);
}

/// <summary>Takes runs off the queue one at a time and executes the Ralph loop.</summary>
public sealed class AgentWorker(
    RunQueue queue,
    RalphLoop loop,
    MarkdownReportWriter reports,
    AgentMetrics metrics,
    ILogger<AgentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            queue.MarkRunning(request.Task.Id);
            metrics.RunStarted(request.Task.Trigger.Kind);
            log.LogWarning("Run {RunId} started ({Trigger}): {Goal}", request.Task.Id, request.Task.Trigger.Kind, request.Task.Goal);

            AgentRun run;
            try
            {
                run = await loop.RunAsync(request.Task, request.Model, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                queue.Abandon(request.Task);
                throw;
            }
            catch (Exception ex)
            {
                // RalphLoop handles its own errors; this is the last net. One broken run must not stop the host
                // (an unhandled exception in a BackgroundService does) and must not leave the namespace "busy" forever.
                log.LogError(ex, "Run {RunId} crashed", request.Task.Id);
                run = new AgentRun(request.Task, request.Task.Trigger.At)
                {
                    Outcome = RunOutcome.Failed,
                    OutcomeReason = $"{ex.GetType().Name}: {ex.Message}",
                    FinishedAt = DateTimeOffset.UtcNow,
                };
            }
            try
            {
                run.ReportPath = await reports.WriteAsync(run, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Writing the report of {RunId} failed", run.Task.Id);
            }

            queue.Complete(run);
            metrics.RunFinished(run);
            log.LogWarning("Run {RunId} finished: {Outcome} ({Reason}), {Iterations} iterations, report {Report}",
                run.Task.Id, run.Outcome, run.OutcomeReason, run.Iterations.Count, run.ReportPath);
        }
    }
}

/// <summary>Optional routine health check (Agent:ScheduleMinutes > 0).</summary>
public sealed class HealthCheckScheduler(RunQueue queue, IOptions<AgentOptions> options, TimeProvider time, ILogger<HealthCheckScheduler> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.ScheduleMinutes;
        if (minutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var ns = options.Value.Namespace;
            var task = queue.NewTask(new TriggerEvent(TriggerKind.Schedule, [], $"routine check every {minutes} min", time.GetUtcNow()), Goals.HealthCheck(ns));
            if (!queue.TryEnqueue(new RunRequest(task, null)))
                log.LogInformation("Scheduled health check skipped: a run for {Namespace} is already active", ns);
        }
    }
}

public static class Goals
{
    public static string ForAlerts(string ns, IEnumerable<AlertInfo> alerts) =>
        $"Alerts are firing in namespace \"{ns}\": {string.Join(", ", alerts.Select(a => a.Name).Distinct())}. " +
        "Find the most likely root cause (not just the symptom), track whether the namespace returns to steady state " +
        "(the alerts resolved and every service's share of bad requests back under 1%), and recommend what a human should do. " +
        "If an available action would clearly help, propose it; the guardrails decide whether it runs.";

    public static string ForIncident(string ns, string? note) =>
        $"An incident was declared for namespace \"{ns}\"{(string.IsNullOrWhiteSpace(note) ? "" : $": \"{note}\"")}. " +
        "Find the most likely root cause (not just the symptom), track whether the namespace returns to steady state " +
        "(every service's share of bad requests back under 1%, all pods ready, no container restarts), and recommend what a human should do. " +
        "If an available action would clearly help, propose it; the guardrails decide whether it runs.";

    public static string HealthCheck(string ns) =>
        $"Routine health check of namespace \"{ns}\". Summarize its state, point out anything abnormal or risky " +
        "(restarts, error rates, latency, saturation, failing dependencies), and recommend what a human should look at.";
}
