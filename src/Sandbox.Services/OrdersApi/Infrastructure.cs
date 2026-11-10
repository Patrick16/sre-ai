using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sandbox.OrdersApi;

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
