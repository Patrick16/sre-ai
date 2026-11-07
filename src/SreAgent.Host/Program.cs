using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

// Phase 5 stub: receives Alertmanager webhooks, logs them and keeps the latest ones in memory.
// In Phase 7 this endpoint becomes the trigger of the agent's Ralph loop.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<AlertInbox>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("sre-agent"))
    .WithMetrics(m => m
        .AddMeter("Microsoft.AspNetCore.Hosting", "System.Runtime")
        .AddMeter(AlertInbox.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapPost("/webhooks/alertmanager", (AlertmanagerWebhook payload, AlertInbox inbox, ILogger<AlertInbox> logger) =>
{
    foreach (var alert in payload.Alerts)
    {
        inbox.Add(alert);
        logger.LogWarning(
            "Alert {Status} {AlertName} severity={Severity} namespace={Namespace} summary={Summary}",
            alert.Status, alert.Label("alertname"), alert.Label("severity"), alert.Label("namespace"),
            alert.Annotations.GetValueOrDefault("summary"));
    }
    return Results.Ok();
});

app.MapGet("/alerts", (AlertInbox inbox) => inbox.Latest());

app.Run();

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

class AlertInbox
{
    public const string MeterName = "SreAgent.Alerts";
    private const int Capacity = 100;
    private readonly ConcurrentQueue<(DateTimeOffset ReceivedAt, AlertmanagerAlert Alert)> _alerts = new();
    private readonly Counter<long> _received;

    public AlertInbox(IMeterFactory factory)
    {
        _received = factory.Create(MeterName)
            .CreateCounter<long>("sre_agent.alerts.received", description: "Alerts received from Alertmanager");
    }

    public void Add(AlertmanagerAlert alert)
    {
        _alerts.Enqueue((DateTimeOffset.UtcNow, alert));
        while (_alerts.Count > Capacity && _alerts.TryDequeue(out _)) { }
        _received.Add(1,
            new KeyValuePair<string, object?>("status", alert.Status),
            new KeyValuePair<string, object?>("alertname", alert.Label("alertname")));
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
