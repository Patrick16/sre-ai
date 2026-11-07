using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SreAgent.Core;

/// <summary>How many tool calls one RALF iteration may make in total, across all requests to the model.</summary>
public sealed class ToolBudget(int max)
{
    private int _used;
    public int Max { get; } = max;
    public int Used => Volatile.Read(ref _used);
    public bool TryTake() => Interlocked.Increment(ref _used) <= Max;
}

/// <summary>
/// Wraps every tool the model can call. The harness - not the framework, not the model - decides what a tool
/// call may cost: a shared step budget per iteration, results cut to a size budget (small local models drown
/// in large outputs), failures returned to the model as text instead of crashing the loop, every call audited.
/// </summary>
public sealed class GuardedTool(
    AIFunction inner,
    string runId,
    int iteration,
    int maxOutputChars,
    ToolBudget budget,
    List<ToolCallRecord> calls,
    IAuditLog audit,
    TimeProvider time) : DelegatingAIFunction(inner)
{
    public const string BudgetExhausted = "TOOL BUDGET EXHAUSTED: no more tool calls are allowed in this iteration. Answer now with the JSON object.";

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!budget.TryTake())
        {
            // Not executed. Also stop the framework's function-calling loop for this request.
            if (FunctionInvokingChatClient.CurrentContext is { } ctx) ctx.Terminate = true;
            await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), runId, iteration, "tool.refused",
                new { tool = Name, reason = "budget", budget.Max }), cancellationToken);
            return BudgetExhausted;
        }

        var started = time.GetTimestamp();
        var args = JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));
        string text;
        var failed = false;
        try
        {
            text = AsText(await base.InvokeCoreAsync(arguments, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            text = $"ERROR: {ex.GetType().Name}: {ex.Message}";
        }

        if (text.Length > maxOutputChars)
            text = text[..maxOutputChars] + $"\n…[truncated: {text.Length - maxOutputChars} more characters; narrow the query]";

        var record = new ToolCallRecord(iteration, Name, args, text.Length > 300 ? text[..300] + "…" : text, failed, time.GetElapsedTime(started));
        lock (calls) calls.Add(record);
        await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), runId, iteration, "tool.called",
            new { tool = Name, arguments = args, failed, chars = text.Length, ms = (int)record.Duration.TotalMilliseconds }), cancellationToken);
        return text;
    }

    private static string AsText(object? result) => result switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        JsonElement e => e.GetRawText(),
        _ => result.ToString() ?? "",
    };
}
