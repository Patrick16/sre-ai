using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Mounted from a ConfigMap as a file: unlike env vars, edits reach the running pod without a restart.
builder.Configuration.AddJsonFile("config/appsettings.k8s.json", optional: true, reloadOnChange: true);
builder.Services.Configure<OrdersOptions>(builder.Configuration.GetSection("Orders"));
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<ApiKeyValidator>();
builder.Services.AddSingleton<Ballast>();

var app = builder.Build();
var pod = Environment.MachineName;

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Served-By"] = pod;
    await next();
});

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGet("/info", (IOptionsMonitor<OrdersOptions> options) => new
{
    service = "orders-api",
    pod,
    version = app.Configuration["APP_VERSION"] ?? "local",
    banner = options.CurrentValue.Banner,
});

var orders = app.MapGroup("/orders").AddEndpointFilter(async (ctx, next) =>
{
    var validator = ctx.HttpContext.RequestServices.GetRequiredService<ApiKeyValidator>();
    return validator.IsValid(ctx.HttpContext.Request.Headers["X-Api-Key"])
        ? await next(ctx)
        : Results.Unauthorized();
});

orders.MapGet("/", (OrderStore store, string? status) => store.List(status));

orders.MapPost("/", (OrderStore store, NewOrder input) =>
{
    var order = store.Add(input.Item, pod);
    return Results.Created($"/orders/{order.Id}", order);
});

orders.MapPost("/{id:guid}/process", (OrderStore store, Guid id) =>
    store.TryProcess(id, out var order) ? Results.Ok(order) : Results.NotFound());

// Holds memory on purpose so the container can be pushed past its memory limit (OOMKilled exercise).
app.MapPost("/debug/allocate", (Ballast ballast, int mb) => new { allocatedMb = ballast.Add(mb) });

app.Run();

record NewOrder(string Item);

record Order(Guid Id, string Item, string Status, string StoredOnPod, DateTimeOffset CreatedAt);

class OrdersOptions
{
    public string Banner { get; set; } = "default";
    public string ApiKeyFile { get; set; } = "/etc/orders-api/secret/api-key";
    public string? ApiKey { get; set; }
}

// State lives in this pod's memory only: every replica has its own list (fixed in Phase 4 with a database).
class OrderStore
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();

    public IEnumerable<Order> List(string? status) =>
        _orders.Values
            .Where(o => status is null || o.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.CreatedAt);

    public Order Add(string item, string pod)
    {
        var order = new Order(Guid.NewGuid(), item, "pending", pod, DateTimeOffset.UtcNow);
        _orders[order.Id] = order;
        return order;
    }

    public bool TryProcess(Guid id, out Order? order)
    {
        if (_orders.TryGetValue(id, out var existing))
        {
            order = existing with { Status = "processed" };
            _orders[id] = order;
            return true;
        }
        order = null;
        return false;
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
