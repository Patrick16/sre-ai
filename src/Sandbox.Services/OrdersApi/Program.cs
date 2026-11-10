using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

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

record NewOrder(string? Item);

static class OrderRules
{
    public const int MaxItemLength = 200;

    /// <returns>what is wrong with the item, or null when it is fine.</returns>
    public static string? Validate(string? item) => item switch
    {
        null => "item is required",
        _ when string.IsNullOrWhiteSpace(item) => "item must not be empty",
        _ when item.Length > MaxItemLength => $"item must be at most {MaxItemLength} characters",
        _ => null,
    };
}

record Order(Guid Id, string Item, string Status, string CreatedByPod, DateTimeOffset CreatedAt);

class OrdersMetrics
{
    public const string MeterName = "Sandbox.Orders";

    public OrdersMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        Created = meter.CreateCounter<long>("sandbox.orders.created", description: "Orders accepted");
        Processed = meter.CreateCounter<long>("sandbox.orders.processed", description: "Orders marked processed");
    }

    public Counter<long> Created { get; }
    public Counter<long> Processed { get; }
}

class OrdersOptions
{
    /// <summary>Processed orders older than this many hours are deleted (the load generator never stops creating them). 0 = keep all.</summary>
    public int RetentionHours { get; set; }

    public string Banner { get; set; } = "default";
    public string ApiKeyFile { get; set; } = "/etc/orders-api/secret/api-key";
    public string? ApiKey { get; set; }
}

class OrderStore(NpgsqlDataSource db)
{
    private const string Columns = "id, item, status, created_by_pod, created_at";
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public Task<List<Order>> ListAsync(string? status, CancellationToken ct) => WithSchemaAsync(async () =>
    {
        await using var cmd = status is null
            // The list of everything shows the NEWEST orders (a page that always shows the first 100 ever created looks
            // frozen); the worker's "pending" queue stays oldest-first (FIFO).
            ? db.CreateCommand($"SELECT {Columns} FROM orders ORDER BY created_at DESC LIMIT 100")
            : db.CreateCommand($"SELECT {Columns} FROM orders WHERE status = $1 ORDER BY created_at LIMIT 100");
        if (status is not null) cmd.Parameters.Add(new() { Value = status.ToLowerInvariant() });

        var result = new List<Order>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }, ct);

    public Task<Order> AddAsync(string item, string pod, CancellationToken ct) => WithSchemaAsync(async () =>
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO orders (id, item, status, created_by_pod) VALUES ($1, $2, 'pending', $3) RETURNING {Columns}");
        cmd.Parameters.Add(new() { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new() { Value = item });
        cmd.Parameters.Add(new() { Value = pod });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }, ct);

    public Task<Order?> TryProcessAsync(Guid id, CancellationToken ct) => WithSchemaAsync(async () =>
    {
        await using var cmd = db.CreateCommand(
            $"UPDATE orders SET status = 'processed' WHERE id = $1 RETURNING {Columns}");
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }, ct);

    // The database can be replaced under us (e.g. its PVC was deleted and recreated empty): if the table
    // is gone, forget the cached "schema ready" flag, recreate it and retry once.
    private async Task<T> WithSchemaAsync<T>(Func<Task<T>> query, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        try
        {
            return await query();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _schemaReady = false;
            await EnsureSchemaAsync(ct);
            return await query();
        }
    }

    /// <returns>how many processed orders older than the cutoff were deleted.</returns>
    public Task<int> DeleteProcessedOlderThanAsync(TimeSpan age, CancellationToken ct) => WithSchemaAsync(async () =>
    {
        await using var cmd = db.CreateCommand("DELETE FROM orders WHERE status = 'processed' AND created_at < now() - $1");
        cmd.Parameters.Add(new() { Value = age });
        return await cmd.ExecuteNonQueryAsync(ct);
    }, ct);

    private static Order Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));

    // Created lazily (not at startup) so the pod starts even while the database is down.
    // The advisory lock serialises the 3 replicas that may all try this at the same moment.
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT pg_advisory_xact_lock(4242);
                CREATE TABLE IF NOT EXISTS orders (
                    id             uuid PRIMARY KEY,
                    item           text NOT NULL,
                    status         text NOT NULL,
                    created_by_pod text NOT NULL,
                    created_at     timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS orders_status_idx ON orders (status);
                CREATE INDEX IF NOT EXISTS orders_created_idx ON orders (created_at);
                """, conn, tx);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}

// Without this the database grows for ever (the load generator creates ~1 order a second) and the 1 Gi volume fills up
// after a few weeks - PostgreSQL then stops accepting writes. Failures are logged, never fatal: it is housekeeping.
class OrderRetention(OrderStore store, IOptionsMonitor<OrdersOptions> options, ILogger<OrderRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var hours = options.CurrentValue.RetentionHours;
            if (hours <= 0) continue;
            try
            {
                var deleted = await store.DeleteProcessedOlderThanAsync(TimeSpan.FromHours(hours), ct);
                if (deleted > 0) logger.LogInformation("Retention: deleted {Count} processed orders older than {Hours} h", deleted, hours);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Retention run failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
        }
    }
}

class DatabaseHealthCheck(NpgsqlDataSource db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

// Reads the key on every request so that rotating the Secret takes effect without a restart.
class ApiKeyValidator(IOptionsMonitor<OrdersOptions> options)
{
    public bool IsValid(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var opts = options.CurrentValue;
        var expected = File.Exists(opts.ApiKeyFile) ? File.ReadAllText(opts.ApiKeyFile).Trim() : opts.ApiKey;
        if (string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}

// Native memory on purpose: the GC caps the managed heap below the container limit (you'd get an
// OutOfMemoryException / HTTP 500), while native allocations go past it and the kernel OOM-kills the container.
unsafe class Ballast
{
    private const int ChunkSize = 1024 * 1024;
    public const int MaxMbPerCall = 512;
    private int _allocatedMb;

    public int Add(int mb)
    {
        for (var i = 0; i < mb; i++)
        {
            var chunk = System.Runtime.InteropServices.NativeMemory.Alloc(ChunkSize);
            new Span<byte>(chunk, ChunkSize).Fill(1); // touch the pages so they count toward the container's memory
            Interlocked.Increment(ref _allocatedMb);
        }
        return _allocatedMb;
    }
}
