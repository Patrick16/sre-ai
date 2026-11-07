using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SreAgent.Core;
using SreAgent.Host;
using SreAgent.LlmClients;
using SreAgent.Tools;

// The SRE agent (Phase 7: observe-only).
//   Alertmanager webhook / schedule / POST /runs  ->  RunQueue  ->  AgentWorker  ->  RalphLoop (RALF iterations)  ->  report
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AlertInbox>();
builder.Services.AddSingleton<AgentMetrics>();
builder.Services.AddAgentTools(builder.Configuration);

builder.Services.AddSingleton<OpenAiCompatibleChatClientFactory>();
builder.Services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<OpenAiCompatibleChatClientFactory>());
builder.Services.AddSingleton<IAuditLog>(sp => new FileAuditLog(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDir, "audit")));
builder.Services.AddSingleton<IRunStateStore>(sp => new FileRunStateStore(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDir, "state")));
builder.Services.AddSingleton(sp => new MarkdownReportWriter(Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDir, "reports")));
builder.Services.AddSingleton<RalfIteration>();
builder.Services.AddSingleton<RalphLoop>();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<HealthCheckScheduler>();

builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("sre-agent"))
    .WithMetrics(m => m
        .AddMeter("Microsoft.AspNetCore.Hosting", "System.Net.Http", "System.Runtime")
        .AddMeter(AgentMetrics.MeterName, OpenAiCompatibleChatClientFactory.TelemetrySource)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapPost("/webhooks/alertmanager", (AlertmanagerWebhook payload, AlertInbox inbox, RunQueue queue, AgentMetrics metrics,
    IOptions<AgentOptions> options, TimeProvider time, ILogger<AlertInbox> logger) =>
{
    var o = options.Value;
    foreach (var alert in payload.Alerts)
    {
        inbox.Add(alert);
        metrics.AlertReceived(alert.Status, alert.Label("alertname"));
        logger.LogWarning("Alert {Status} {AlertName} severity={Severity} namespace={Namespace} summary={Summary}",
            alert.Status, alert.Label("alertname"), alert.Label("severity"), alert.Label("namespace"), alert.Annotations.GetValueOrDefault("summary"));
    }

    var firing = payload.Alerts
        .Where(a => a.Status == "firing" && a.Label("namespace") == o.Namespace && o.Severities.Contains(a.Label("severity") ?? ""))
        .Select(a => new AlertInfo(a.Label("alertname") ?? "?", a.Label("severity") ?? "", a.Label("namespace"),
            a.Annotations.GetValueOrDefault("summary"), a.Fingerprint, a.Labels, a.StartsAt))
        .ToList();
    if (firing.Count == 0) return Results.Ok(new { started = false, reason = "no firing alert of the agent's namespace/severities" });

    var task = queue.NewTask(new TriggerEvent(TriggerKind.Alert, firing, null, time.GetUtcNow()), Goals.ForAlerts(o.Namespace, firing));
    if (queue.TryEnqueue(new RunRequest(task, null))) return Results.Ok(new { started = true, runId = task.Id });

    metrics.Deduplicated();
    return Results.Ok(new { started = false, reason = $"run {queue.ActiveRunFor(o.Namespace)} is already looking at {o.Namespace}" });
});

// Manual run: POST /runs  {"goal": "...", "model": "qwen/qwen3.5-9b"}  (both optional)
app.MapPost("/runs", (ManualRun? body, RunQueue queue, OpenAiCompatibleChatClientFactory llm, IOptions<AgentOptions> options, TimeProvider time) =>
{
    var ns = options.Value.Namespace;
    if (body?.Model is { Length: > 0 } m && !llm.Models.Contains(m, StringComparer.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = $"model '{m}' is not allowed", allowed = llm.Models });
    var task = queue.NewTask(new TriggerEvent(TriggerKind.Manual, [], "started by a human", time.GetUtcNow()),
        string.IsNullOrWhiteSpace(body?.Goal) ? Goals.HealthCheck(ns) : body.Goal);
    return queue.TryEnqueue(new RunRequest(task, body?.Model))
        ? Results.Accepted($"/runs/{task.Id}", new { runId = task.Id })
        : Results.Conflict(new { error = $"run {queue.ActiveRunFor(ns)} is already active for {ns}" });
});

app.MapGet("/runs", (RunQueue queue) => queue.List());
app.MapGet("/runs/{id}", (string id, RunQueue queue) => queue.Get(id) is { } r ? Results.Ok(r) : Results.NotFound());
app.MapGet("/runs/{id}/report", (string id, RunQueue queue) =>
    queue.Get(id)?.ReportPath is { } path && File.Exists(path)
        ? Results.Text(File.ReadAllText(path), "text/markdown; charset=utf-8")
        : Results.NotFound());
app.MapGet("/models", (OpenAiCompatibleChatClientFactory llm, IOptions<LlmOptions> o) => new { @default = o.Value.Model, allowed = llm.Models });
app.MapGet("/alerts", (AlertInbox inbox) => inbox.Latest());

app.Run();

record ManualRun(string? Goal, string? Model);

// https://prometheus.io/docs/alerting/latest/configuration/#webhook_config
record AlertmanagerWebhook(string Status, string Receiver, List<AlertmanagerAlert> Alerts);

record AlertmanagerAlert(
    string Status,
    Dictionary<string, string> Labels,
    Dictionary<string, string> Annotations,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? GeneratorUrl,
    string Fingerprint)
{
    public string? Label(string name) => Labels.GetValueOrDefault(name);
}

/// <summary>The last 100 alerts received, newest first (GET /alerts) - for humans and debugging.</summary>
class AlertInbox
{
    private const int Capacity = 100;
    private readonly ConcurrentQueue<(DateTimeOffset ReceivedAt, AlertmanagerAlert Alert)> _alerts = new();

    public void Add(AlertmanagerAlert alert)
    {
        _alerts.Enqueue((DateTimeOffset.UtcNow, alert));
        while (_alerts.Count > Capacity && _alerts.TryDequeue(out _)) { }
    }

    public IEnumerable<object> Latest() => _alerts.Reverse().Select(a => new
    {
        a.ReceivedAt,
        a.Alert.Status,
        AlertName = a.Alert.Label("alertname"),
        Severity = a.Alert.Label("severity"),
        a.Alert.Labels,
        a.Alert.Annotations,
        a.Alert.StartsAt,
    });
}
