using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.AddFaults(); // Phase 9: chaos "bad modes" from /app/faults/faults.json (all off by default)

builder.Services.AddSingleton<Heartbeat>();
builder.Services.AddSingleton<WorkerMetrics>();
builder.Services.AddHostedService<OrderProcessor>();
builder.Services.AddHealthChecks().AddCheck<HeartbeatHealthCheck>("processing-loop", tags: ["live"]);
builder.Services.AddHttpClient("orders-api", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["OrdersApi:BaseUrl"] ?? throw new InvalidOperationException("OrdersApi:BaseUrl is not set"));
    client.DefaultRequestHeaders.Add("X-Api-Key", config["OrdersApi:ApiKey"]);
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("worker"))
    .WithMetrics(m => m
        .AddMeter("Microsoft.AspNetCore.Hosting", "System.Net.Http", "System.Runtime")
        .AddMeter(WorkerMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");

app.UseFaults();

// Liveness = "is my own loop still turning", never "is orders-api up": a dependency outage must not restart us.
app.MapHealthChecks("/health/live", new() { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = _ => false });

app.Run();

record PendingOrder(Guid Id, string Item);

class WorkerMetrics
{
    public const string MeterName = "Sandbox.Worker";

    public WorkerMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        Polls = meter.CreateCounter<long>("sandbox.worker.polls", description: "Polling iterations by result (ok / http_error / failed)");
        Processed = meter.CreateCounter<long>("sandbox.worker.orders.processed", description: "Orders the worker processed");
    }

    public Counter<long> Polls { get; }
    public Counter<long> Processed { get; }
}

class Heartbeat
{
    private long _lastTicks = DateTimeOffset.UtcNow.UtcTicks;
    public DateTimeOffset Last => new(Interlocked.Read(ref _lastTicks), TimeSpan.Zero);
    public void Beat() => Interlocked.Exchange(ref _lastTicks, DateTimeOffset.UtcNow.UtcTicks);
}

class HeartbeatHealthCheck(Heartbeat heartbeat, IConfiguration config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Worker:IntervalSeconds", 5));
        var age = DateTimeOffset.UtcNow - heartbeat.Last;
        return Task.FromResult(age < interval * 3
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"processing loop stalled for {age.TotalSeconds:F0}s"));
    }
}

class OrderProcessor(IHttpClientFactory factory, Heartbeat heartbeat, WorkerMetrics metrics, IConfiguration config, ILogger<OrderProcessor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Worker:IntervalSeconds", 5));
        logger.LogInformation("Worker started, polling orders-api every {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // ANY failure of one polling round (network, timeout, a proxy answering 200 with HTML -> JsonException ...)
                // is logged and counted; the next round tries again. An unhandled exception here would stop the
                // BackgroundService and take the whole process down with it.
                metrics.Polls.Add(1, new KeyValuePair<string, object?>("result", "failed"));
                logger.LogWarning("polling round failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ProcessPendingAsync(CancellationToken ct)
    {
        var client = factory.CreateClient("orders-api");
        using var response = await client.GetAsync("/orders?status=pending", ct);
        if (!response.IsSuccessStatusCode)
        {
            metrics.Polls.Add(1, new KeyValuePair<string, object?>("result", "http_error"));
            logger.LogWarning("orders-api returned {Status}", (int)response.StatusCode);
            return;
        }
        metrics.Polls.Add(1, new KeyValuePair<string, object?>("result", "ok"));

        var servedBy = response.Headers.TryGetValues("X-Served-By", out var v) ? v.First() : "?";
        var pending = await response.Content.ReadFromJsonAsync<PendingOrder[]>(ct) ?? [];
        if (pending.Length > 0)
            logger.LogInformation("Polled {Pod}: {Count} pending", servedBy, pending.Length);
        foreach (var order in pending)
        {
            // Once per order, not only once per round: with a long queue and a slow orders-api the loop is slow but
            // alive, and liveness must not restart us because a DEPENDENCY is slow.
            heartbeat.Beat();
            using var result = await client.PostAsync($"/orders/{order.Id}/process", null, ct);
            if (result.IsSuccessStatusCode) metrics.Processed.Add(1);
            logger.LogInformation("Order {Id} ({Item}) listed by {ListedBy}: process -> {Status}",
                order.Id, order.Item, servedBy, (int)result.StatusCode);
        }
    }
}
