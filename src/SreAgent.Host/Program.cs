using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using SreAgent.Core;
using SreAgent.Host;
using SreAgent.LlmClients;
using SreAgent.Tools;

// The SRE agent.
//   Alertmanager webhook / schedule / POST /runs  ->  RunQueue  ->  AgentWorker  ->  RalphLoop (RALF iterations)  ->  report
// Phase 8: the Lead step may run ONE allowlisted action per iteration, through the guardrails (docs/06).
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
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return new MarkdownReportWriter(Path.Combine(o.DataDir, "reports"));
});

// Actions: only when at least one type is allowlisted; otherwise ActionLead isn't registered and the agent observes.
var actions = builder.Configuration.GetSection(ActionsOptions.Section).Get<ActionsOptions>() ?? new ActionsOptions();
builder.Services.AddSingleton<ApprovalGate>();
builder.Services.AddSingleton<IApprovalGate>(sp => sp.GetRequiredService<ApprovalGate>());
if (actions.Allowed.Count > 0)
{
    builder.Services.AddSingleton(actions.ToSettings());
    builder.Services.AddSingleton<IActionLedger>(sp => new FileActionLedger(
        Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDir, "actions-ledger.json"), sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<Guardrails>();
    builder.Services.AddSingleton<ActionLead>();
}
builder.Services.AddSingleton(sp => new RunHistory(
    Path.Combine(sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDir, "runs"), sp.GetRequiredService<ILogger<RunHistory>>()));
builder.Services.AddSingleton<RunTraceStore>();
builder.Services.AddSingleton<IRunObserver>(sp => sp.GetRequiredService<RunTraceStore>());
builder.Services.AddSingleton<RalfIteration>();
builder.Services.AddSingleton<RunSynthesizer>();
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

// RunState files of old runs are only diagnostics (a run is never resumed): don't let them pile up on the PVC.
{
    var o = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
    var removed = DataRetention.PruneOldFiles(Path.Combine(o.DataDir, "state"), TimeSpan.FromDays(o.StateRetentionDays), DateTime.UtcNow);
    if (removed > 0) app.Logger.LogInformation("Removed {Count} RunState file(s) older than {Days} days", removed, o.StateRetentionDays);
    // Before RunQueue is created (it loads what is left): the console's run list and traces.
    DataRetention.PruneOldFiles(Path.Combine(o.DataDir, "runs"), TimeSpan.FromDays(o.HistoryRetentionDays), DateTime.UtcNow);
}

app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapPost("/webhooks/alertmanager", (AlertmanagerWebhook payload, AlertInbox inbox, RunQueue queue, AgentMetrics metrics,
    OpenAiCompatibleChatClientFactory llm, IOptions<AgentOptions> options, TimeProvider time, ILogger<AlertInbox> logger) =>
{
    var o = options.Value;
    foreach (var alert in payload.Alerts)
    {
        inbox.Add(alert);
        if (alert.Status == "resolved") queue.ForgetHandled(alert.Fingerprint);
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

    var fingerprints = firing.Select(a => a.Fingerprint).ToList();
    if (queue.AlreadyHandled(fingerprints))
    {
        metrics.Deduplicated();
        return Results.Ok(new { started = false, reason = $"these alerts already started a run within the last {o.AlertSuppressionMinutes} min (still firing: Alertmanager's repeat)" });
    }

    var task = queue.NewTask(new TriggerEvent(TriggerKind.Alert, firing, null, time.GetUtcNow()), Goals.ForAlerts(o.Namespace, firing));
    if (queue.TryEnqueue(RunRequest.For(task, llm)))
    {
        queue.RememberHandled(fingerprints);
        return Results.Ok(new { started = true, runId = task.Id });
    }

    metrics.Deduplicated();
    return Results.Ok(new { started = false, reason = $"run {queue.ActiveRunFor(o.Namespace)} is already looking at {o.Namespace}" });
});

// Manual run: POST /runs  {"goal": "...", "model": "qwen/qwen3.5-9b"}  (both optional) - a one-iteration health check.
// Incident:   POST /runs  {"incident": true, "note": "users report errors on the site"} - works until steady state,
//             like an alert run (Phase 9: game days declare incidents instead of waiting 10-30 min for a burn-rate page).
//             optional for both: "provider" + "model" (GET /llm lists them) and "mode" (DryRun | Approve | Auto) - the
//             action ceiling of THIS run instead of the global one; an action's own mode still applies.
app.MapPost("/runs", (HttpContext http, ManualRun? body, RunQueue queue, OpenAiCompatibleChatClientFactory llm, IServiceProvider sp, IOptions<AgentOptions> options, TimeProvider time) =>
{
    if (!ApiToken.IsValid(options.Value.ApiToken, http.Request.Headers[ApiToken.Header])) return Results.Unauthorized();
    var ns = options.Value.Namespace;
    var spec = LlmRef.Format(body?.Provider, body?.Model);
    if (spec is not null && llm.Resolve(spec).Error is { } llmError) return Results.BadRequest(new { error = llmError });
    ActionMode? mode = null;
    if (!string.IsNullOrWhiteSpace(body?.Mode))
    {
        if (!Enum.TryParse<ActionMode>(body.Mode, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return Results.BadRequest(new { error = $"mode must be one of: {string.Join(", ", Enum.GetNames<ActionMode>())}" });
        if (sp.GetService<ActionSettings>() is null)
            return Results.BadRequest(new { error = "no action is allowlisted (Actions__Allowed__*): a run cannot have an action mode" });
        mode = parsed;
    }
    var task = body?.Incident == true
        ? queue.NewTask(new TriggerEvent(TriggerKind.Incident, [], body.Note ?? "incident declared by a human", time.GetUtcNow()),
            string.IsNullOrWhiteSpace(body.Goal) ? Goals.ForIncident(ns, body.Note) : body.Goal, mode: mode)
        : queue.NewTask(new TriggerEvent(TriggerKind.Manual, [], string.IsNullOrWhiteSpace(body?.Note) ? "started by a human" : body.Note, time.GetUtcNow()),
            string.IsNullOrWhiteSpace(body?.Goal) ? Goals.HealthCheck(ns) : body.Goal, mode: mode);
    return queue.TryEnqueue(RunRequest.For(task, llm, spec))
        ? Results.Accepted($"/runs/{task.Id}", new { runId = task.Id })
        : Results.Conflict(new { error = $"run {queue.ActiveRunFor(ns)} is already active for {ns}" });
});

app.MapGet("/runs", (RunQueue queue) => queue.List());
app.MapGet("/runs/{id}", (string id, RunQueue queue) => queue.Get(id) is { } r ? Results.Ok(r) : Results.NotFound());
// The console's live view: the run plus every iteration finished so far (in memory, the last 30 runs).
app.MapGet("/runs/{id}/trace", (string id, RunQueue queue, RunTraceStore traces) =>
    queue.Get(id) is { } run ? Results.Ok(new { run, iterations = traces.Get(id) }) : Results.NotFound());
app.MapGet("/runs/{id}/report", (string id, RunQueue queue) =>
    queue.Get(id)?.ReportPath is { } path && File.Exists(path)
        ? Results.Text(File.ReadAllText(path), "text/markdown; charset=utf-8")
        : Results.NotFound());
app.MapGet("/models", (OpenAiCompatibleChatClientFactory llm, IOptions<LlmOptions> o) => new { @default = o.Value.Model, allowed = llm.Models });
app.MapGet("/alerts", (AlertInbox inbox) => inbox.Latest());

// LLM providers. Which providers exist (and their URLs) is configuration; a human may choose among them, pick the
// default model for runs that don't choose (alerts, schedule) and supply a key. Keys are write-only: no endpoint
// returns one, they are not logged, and a key set here lives in memory until the agent restarts.
app.MapGet("/llm", (OpenAiCompatibleChatClientFactory llm) =>
    new { @default = new { provider = llm.Default.Provider, model = llm.Default.Model }, providers = llm.Providers });
app.MapGet("/llm/providers/{name}/models", async (string name, OpenAiCompatibleChatClientFactory llm, CancellationToken ct) =>
{
    var (models, error) = await llm.ListModelsAsync(name, ct);
    return Results.Ok(new { models, error });
});
app.MapPut("/llm/default", (HttpContext http, LlmChoice body, OpenAiCompatibleChatClientFactory llm, IOptions<AgentOptions> options, ILogger<OpenAiCompatibleChatClientFactory> logger) =>
{
    if (!ApiToken.IsValid(options.Value.ApiToken, http.Request.Headers[ApiToken.Header])) return Results.Unauthorized();
    if (llm.SetDefault(body.Provider, body.Model) is { } error) return Results.BadRequest(new { error });
    logger.LogWarning("Default LLM changed to {Provider} / {Model}", llm.Default.Provider, llm.Default.Model);
    return Results.Ok(new { provider = llm.Default.Provider, model = llm.Default.Model });
});
app.MapPut("/llm/providers/{name}/key", (HttpContext http, string name, KeyBody body, OpenAiCompatibleChatClientFactory llm, IOptions<AgentOptions> options, ILogger<OpenAiCompatibleChatClientFactory> logger) =>
{
    if (!ApiToken.IsValid(options.Value.ApiToken, http.Request.Headers[ApiToken.Header])) return Results.Unauthorized();
    if (!llm.SetKey(name, body.ApiKey)) return Results.NotFound(new { error = $"provider '{name}' is not configured" });
    logger.LogWarning("API key of LLM provider {Provider} was {Change} at runtime", name, string.IsNullOrWhiteSpace(body.ApiKey) ? "removed" : "set");
    return Results.Ok(new { provider = name, keySet = llm.Providers.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).KeySet });
});

// Approve mode: what waits for a human, and the decision.
app.MapGet("/actions", (IServiceProvider sp) => sp.GetService<ActionSettings>() is { } s
    ? Results.Ok(new { s.Enabled, mode = s.Mode.ToString(), allowed = s.Allowed.ToDictionary(kv => kv.Key, kv => s.EffectiveMode(kv.Key).ToString()), s.MaxPerRun, s.MaxPerWindow, window = s.Window.ToString(), cooldown = s.Cooldown.ToString() })
    : Results.Ok(new { enabled = false, reason = "no action is allowlisted (Actions__Allowed__*): observe-only" }));
// The global ceiling, changed by a human from the console. In memory on purpose: a restart goes back to Actions__Mode.
app.MapPut("/actions/mode", (HttpContext http, ModeBody body, IServiceProvider sp, IOptions<AgentOptions> options, ILogger<ApprovalGate> logger) =>
{
    if (!ApiToken.IsValid(options.Value.ApiToken, http.Request.Headers[ApiToken.Header])) return Results.Unauthorized();
    if (sp.GetService<ActionSettings>() is not { } settings)
        return Results.Conflict(new { error = "no action is allowlisted (Actions__Allowed__*): there is no mode to change" });
    if (!Enum.TryParse<ActionMode>(body.Mode, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        return Results.BadRequest(new { error = $"mode must be one of: {string.Join(", ", Enum.GetNames<ActionMode>())}" });
    var previous = settings.Mode;
    settings.Mode = mode;
    logger.LogWarning("Action ceiling changed from {Previous} to {Mode} by {By}", previous, mode, body.By ?? "unknown");
    return Results.Ok(new { mode = mode.ToString(), previous = previous.ToString() });
});
app.MapGet("/console", () =>
{
    using var stream = typeof(RunTraceStore).Assembly.GetManifestResourceStream("console.html")
        ?? throw new InvalidOperationException("console.html is not embedded in the agent");
    using var reader = new StreamReader(stream);
    return Results.Content(reader.ReadToEnd(), "text/html; charset=utf-8");
});
app.MapGet("/", () => Results.Redirect("/console"));
app.MapGet("/approvals", (ApprovalGate gate) => gate.Pending());
app.MapPost("/approvals/{id}", (HttpContext http, string id, ApprovalBody body, ApprovalGate gate, IOptions<AgentOptions> options, ILogger<ApprovalGate> logger) =>
{
    if (!ApiToken.IsValid(options.Value.ApiToken, http.Request.Headers[ApiToken.Header])) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.By)) return Results.BadRequest(new { error = "\"by\" (who decides) is required - it goes to the audit log" });
    if (!gate.Decide(id, body.Approve, body.By)) return Results.NotFound(new { error = $"no pending approval '{id}' (expired or decided)" });
    logger.LogWarning("Approval {Id}: {Decision} by {By}", id, body.Approve ? "approved" : "rejected", body.By);
    return Results.Ok(new { id, approved = body.Approve, body.By });
});

app.Run();

record ManualRun(string? Goal, string? Model, bool? Incident, string? Note, string? Provider = null, string? Mode = null);

record LlmChoice(string? Provider, string? Model);

record KeyBody(string? ApiKey);

record ApprovalBody(bool Approve, string? By);

record ModeBody(string Mode, string? By);

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
