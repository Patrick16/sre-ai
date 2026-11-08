using System.Net.Http.Json;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
builder.AddFaults(); // Phase 9: chaos "bad modes" from /app/faults/faults.json (all off by default)

builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("frontend"))
    .WithMetrics(m => m
        // System.Net.Http: outgoing calls to orders-api (latency/errors of the dependency, seen from the caller).
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "System.Runtime")
        .AddPrometheusExporter());
builder.Services.AddHttpClient("orders-api", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    client.BaseAddress = new Uri(config["OrdersApi:BaseUrl"] ?? throw new InvalidOperationException("OrdersApi:BaseUrl is not set"));
    client.DefaultRequestHeaders.Add("X-Api-Key", config["OrdersApi:ApiKey"]);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();
var pod = Environment.MachineName;

// Only on the metrics port, which the Service/Ingress for traffic doesn't route: metrics must not be public.
// LocalPort (the socket actually used), not the Host header, which a client controls.
app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");

app.UseFaults();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGet("/", async (IHttpClientFactory factory, IConfiguration config) =>
{
    var client = factory.CreateClient("orders-api");
    try
    {
        using var response = await client.GetAsync("/orders");
        if (!response.IsSuccessStatusCode)
            return Results.Problem($"orders-api returned {(int)response.StatusCode}", statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(new
        {
            greeting = config["Frontend:Greeting"],
            frontendPod = pod,
            version = config["APP_VERSION"] ?? "local",
            ordersApiPod = response.Headers.TryGetValues("X-Served-By", out var v) ? v.First() : null,
            orders = await response.Content.ReadFromJsonAsync<object[]>(),
        });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"orders-api unreachable: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/order", async (IHttpClientFactory factory, string item) =>
{
    var client = factory.CreateClient("orders-api");
    try
    {
        using var response = await client.PostAsJsonAsync("/orders", new { item });
        var body = await response.Content.ReadAsStringAsync();
        return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"orders-api unreachable: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
