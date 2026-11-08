using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

// Phase 9: "bad modes" for chaos experiments, shared by all sandbox services (linked into each project).
//
// Configured by a file from a ConfigMap (chart value `faults:`, rendered to /app/faults/faults.json). A directory
// mount (not subPath), so kubelet refreshes the file in the running pod (~1 min) and reloadOnChange picks it up:
// faults switch on and off WITHOUT a restart - a restart would itself "fix" a leak and hide the effect.
//   helm upgrade orders-api ... --set faults.errorRatio=0.3   # 30% of requests answer 500
//
// The services don't announce the fault in their logs: the agent has to find it from symptoms, like a real bug.

/// <summary>All zero = healthy.</summary>
public sealed class FaultOptions
{
    /// <summary>Extra delay added to requests (ms), e.g. 400 against a 250 ms latency SLO.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Share of requests that get the delay (0..1).</summary>
    public double LatencyRatio { get; set; } = 1;

    /// <summary>Share of requests answered with HTTP 500 (0..1).</summary>
    public double ErrorRatio { get; set; }

    /// <summary>Native memory leaked per minute, until the container is OOMKilled (and again after each restart).</summary>
    public int LeakMbPerMinute { get; set; }
}

public static class FaultExtensions
{
    public static WebApplicationBuilder AddFaults(this WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("faults/faults.json", optional: true, reloadOnChange: true);
        builder.Services.Configure<FaultOptions>(builder.Configuration.GetSection("Faults"));
        builder.Services.AddHostedService<MemoryLeak>();
        return builder;
    }

    /// <summary>
    /// After routing (the endpoint is already chosen, so the injected 500s and delays carry the route label and count
    /// in the SLI like real ones). Never on health probes or the metrics port: the fault is in the service's work,
    /// not in Kubernetes' view of it.
    /// </summary>
    public static WebApplication UseFaults(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var f = ctx.RequestServices.GetRequiredService<IOptionsMonitor<FaultOptions>>().CurrentValue;
            var affected = ctx.GetEndpoint() is not null
                           && ctx.Connection.LocalPort != 9090
                           && !ctx.Request.Path.StartsWithSegments("/health");
            if (affected)
            {
                if (f.LatencyMs > 0 && Random.Shared.NextDouble() < f.LatencyRatio)
                    await Task.Delay(f.LatencyMs, ctx.RequestAborted);
                if (f.ErrorRatio > 0 && Random.Shared.NextDouble() < f.ErrorRatio)
                {
                    ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Sandbox.Unhandled").LogError(
                        "Unhandled exception while processing {Method} {Path}: System.InvalidOperationException: Operation is not valid due to the current state of the object.",
                        ctx.Request.Method, ctx.Request.Path);
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
            }
            await next();
        });
        return app;
    }
}

/// <summary>Leaks native memory (the GC can't cap it), so the kernel OOM-kills the container at its limit.</summary>
internal sealed class MemoryLeak(IOptionsMonitor<FaultOptions> options) : BackgroundService
{
    private const int Chunk = 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(6));
        while (await timer.WaitForNextTickAsync(ct))
        {
            // 1/10 of the per-minute amount every 6 s: a steady ramp on the memory graph, not a step.
            var mb = options.CurrentValue.LeakMbPerMinute / 10.0;
            for (var i = 0; i < (int)Math.Ceiling(mb) && mb > 0; i++) LeakOneChunk();
        }
    }

    private static unsafe void LeakOneChunk()
    {
        var p = NativeMemory.Alloc(Chunk);
        new Span<byte>(p, Chunk).Fill(1); // touch the pages: untouched memory doesn't count
    }
}
