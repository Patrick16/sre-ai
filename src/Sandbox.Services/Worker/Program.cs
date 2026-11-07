using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Heartbeat>();
builder.Services.AddHostedService<OrderProcessor>();
builder.Services.AddHealthChecks().AddCheck<HeartbeatHealthCheck>("processing-loop", tags: ["live"]);
builder.Services.AddHttpClient("orders-api", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["OrdersApi:BaseUrl"] ?? throw new InvalidOperationException("OrdersApi:BaseUrl is not set"));
    client.DefaultRequestHeaders.Add("X-Api-Key", config["OrdersApi:ApiKey"]);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

// Liveness = "is my own loop still turning", never "is orders-api up": a dependency outage must not restart us.
app.MapHealthChecks("/health/live", new() { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = _ => false });

app.Run();

record PendingOrder(Guid Id, string Item);

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

class OrderProcessor(IHttpClientFactory factory, Heartbeat heartbeat, IConfiguration config, ILogger<OrderProcessor> logger)
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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("orders-api call failed: {Message}", ex.Message);
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
            logger.LogWarning("orders-api returned {Status}", (int)response.StatusCode);
            return;
        }

        var servedBy = response.Headers.TryGetValues("X-Served-By", out var v) ? v.First() : "?";
        var pending = await response.Content.ReadFromJsonAsync<PendingOrder[]>(ct) ?? [];
        logger.LogInformation("Polled {Pod}: {Count} pending", servedBy, pending.Length);
        foreach (var order in pending)
        {
            using var result = await client.PostAsync($"/orders/{order.Id}/process", null, ct);
            logger.LogInformation("Order {Id} ({Item}) listed by {ListedBy}: process -> {Status}",
                order.Id, order.Item, servedBy, (int)result.StatusCode);
        }
    }
}
