using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Sandbox.OrdersApi;

var builder = WebApplication.CreateBuilder(args);
builder.AddFaults(); // Phase 9: chaos "bad modes" from /app/faults/faults.json (all off by default)

// Mounted from a ConfigMap as a file: unlike env vars, edits reach the running pod without a restart.
builder.Configuration.AddJsonFile("config/appsettings.k8s.json", optional: true, reloadOnChange: true);
builder.Services.Configure<OrdersOptions>(builder.Configuration.GetSection("Orders"));
builder.Services.AddSingleton(sp =>
{
    var db = sp.GetRequiredService<IConfiguration>().GetSection("Orders:Db");
    var cs = new NpgsqlConnectionStringBuilder
    {
        Host = db["Host"] ?? "localhost",
        Port = db.GetValue("Port", 5432),
        Database = db["Database"] ?? "orders",
        Username = db["Username"] ?? "orders",
        Password = db["Password"],
        Timeout = 5,
        CommandTimeout = 5,
        // Npgsql otherwise tries Kerberos/GSSAPI encryption first; the image has no libgssapi, so every new
        // connection printed "libgssapi_krb5.so.2: cannot open shared object file" to stderr.
        GssEncryptionMode = GssEncryptionMode.Disable,
    };
    return NpgsqlDataSource.Create(cs);
});
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<Ballast>();
builder.Services.AddHostedService<OrderRetention>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["db"]);
builder.Services.AddSingleton<OrdersMetrics>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("orders-api"))
    .WithMetrics(m => m
        // Built-in .NET meters: HTTP server, Kestrel, GC/threads/memory, Npgsql connection pool.
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Runtime", "Npgsql")
        .AddMeter(OrdersMetrics.MeterName)
        .AddPrometheusExporter());

var app = builder.Build();

// Only on the metrics port, which the Service/Ingress for traffic doesn't route: metrics must not be public.
// LocalPort (the socket actually used), not the Host header, which a client controls.
app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 && ctx.Request.Path == "/metrics");
var pod = Environment.MachineName;

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Served-By"] = pod;
    await next();
});

app.UseFaults();

// Probes deliberately ignore the database: if it is down, every replica would go unready/restart at once
// and the outage would look like an orders-api problem. /health/db is for humans, dashboards and the agent.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = _ => false });
app.MapHealthChecks("/health/db", new() { Predicate = c => c.Tags.Contains("db") });

app.MapGet("/info", (IOptionsMonitor<OrdersOptions> options) => new
{
    service = "orders-api",
    pod,
    version = app.Configuration["APP_VERSION"] ?? "local",
    banner = options.CurrentValue.Banner,
});

async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
{
    var validator = ctx.HttpContext.RequestServices.GetRequiredService<ApiKeyValidator>();
    return validator.IsValid(ctx.HttpContext.Request.Headers["X-Api-Key"])
        ? await next(ctx)
        : Results.Unauthorized();
}

var orders = app.MapGroup("/orders")
    .AddEndpointFilter(RequireApiKey)
    .AddEndpointFilter(async (ctx, next) =>
    {
        try
        {
            return await next(ctx);
        }
        // ORDER MATTERS: PostgresException derives from NpgsqlException, so this clause must stay ABOVE the NpgsqlException one
        // below, or bad client data would again be reported as "database unavailable" (503).
        catch (PostgresException ex) when (ex.SqlState is { } state && (state.StartsWith("22") || state.StartsWith("23")))
        {
            // Bad data (class 22) or a violated constraint (class 23) is the CLIENT's mistake, not an outage:
            // answering 503 "database unavailable" burned the SLO and sent the on-call after a healthy database.
            ctx.HttpContext.RequestServices.GetRequiredService<ILogger<OrderStore>>()
                .LogWarning("Rejected by the database ({SqlState}): {Message}", ex.SqlState, ex.MessageText);
            return Results.Problem("the database rejected the data", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            ctx.HttpContext.RequestServices.GetRequiredService<ILogger<OrderStore>>()
                .LogError("Database unavailable: {Message}", ex.Message);
            return Results.Problem("database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

orders.MapGet("/", (OrderStore store, string? status, CancellationToken ct) => store.ListAsync(status, ct));

orders.MapPost("/", async (OrderStore store, OrdersMetrics metrics, NewOrder? input, CancellationToken ct) =>
{
    if (OrderRules.Validate(input?.Item) is { } problem)
        return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
    var order = await store.AddAsync(input!.Item!.Trim(), pod, ct);
    metrics.Created.Add(1);
    return Results.Created($"/orders/{order.Id}", order);
});

orders.MapPost("/{id:guid}/process", async (OrderStore store, OrdersMetrics metrics, Guid id, CancellationToken ct) =>
{
    if (await store.TryProcessAsync(id, ct) is not { } order) return Results.NotFound();
    metrics.Processed.Add(1);
    return Results.Ok(order);
});

// Holds memory on purpose so the container can be pushed past its memory limit (OOMKilled exercise).
// Behind the API key like the rest (this path is reachable through the Ingress), and the size is bounded.
app.MapGroup("/debug")
    .AddEndpointFilter(RequireApiKey)
    .MapPost("/allocate", (Ballast ballast, int mb) =>
        mb is < 1 or > Ballast.MaxMbPerCall
            ? Results.Problem($"mb must be between 1 and {Ballast.MaxMbPerCall}", statusCode: StatusCodes.Status400BadRequest)
            : Results.Ok(new { allocatedMb = ballast.Add(mb) }));

app.Run();
