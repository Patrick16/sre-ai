using Microsoft.Extensions.Options;
using Npgsql;

namespace Sandbox.OrdersApi;

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
