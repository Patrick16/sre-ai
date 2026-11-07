using System.Text.Json;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>Append-only JSON Lines, one file per day: {DataDir}/audit/2026-11-08.jsonl (docs/08).</summary>
public sealed class FileAuditLog(string directory) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = auditEvent.Ts,
            runId = auditEvent.RunId,
            iteration = auditEvent.Iteration,
            @event = auditEvent.Event,
            data = auditEvent.Data,
        }, Json);
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, $"{auditEvent.Ts:yyyy-MM-dd}.jsonl"), line + "\n", ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
