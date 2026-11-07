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
    private readonly Channel<RunRequest> _channel = Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ConcurrentDictionary<string, RunSummary> _runs = new();
    private readonly ConcurrentDictionary<string, string> _activeByNamespace = new();

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
        if (!_activeByNamespace.TryAdd(request.Task.Namespace, request.Task.Id)) return false;
        if (!_channel.Writer.TryWrite(request))
        {
            _activeByNamespace.TryRemove(request.Task.Namespace, out _);
            return false;
        }
        _runs[request.Task.Id] = new RunSummary(request.Task.Id, request.Task.Trigger.Kind.ToString(), "queued", time.GetUtcNow(), null, 0, null, null, null, null);
        return true;
    }

    public void MarkRunning(string id) =>
        _runs.AddOrUpdate(id, _ => throw new InvalidOperationException(), (_, s) => s with { Status = "running" });

    public void Complete(AgentRun run)
    {
        _activeByNamespace.TryRemove(run.Task.Namespace, out _);
        _runs.AddOrUpdate(run.Task.Id, _ => throw new InvalidOperationException(), (_, s) => s with
        {
            Status = "finished",
            FinishedAt = run.FinishedAt,
            Iterations = run.Iterations.Count,
            Hypothesis = run.Synthesis?.RootCause ?? run.Iterations.LastOrDefault()?.Analysis.Hypothesis,
            Outcome = run.Outcome?.ToString(),
            OutcomeReason = run.OutcomeReason,
            ReportPath = run.ReportPath,
        });
    }

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

            var run = await loop.RunAsync(request.Task, request.Model, stoppingToken);
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
        "You can only observe.";

    public static string HealthCheck(string ns) =>
        $"Routine health check of namespace \"{ns}\". Summarize its state, point out anything abnormal or risky " +
        "(restarts, error rates, latency, saturation, failing dependencies), and recommend what a human should look at.";
}
