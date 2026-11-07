using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SreAgent.Core;

/// <summary>
/// After the loop: one model call without tools that reads the whole run (every iteration's hypothesis and evidence,
/// all verified facts, the outcome) and writes the conclusion. Without it the report would take the last iteration's
/// opinion - which, after recovery, sees only the aftermath and in a real test named a wrong cause.
/// Best effort: if it fails, the report falls back to the last iteration.
/// </summary>
public sealed class RunSynthesizer(IChatClientFactory llm, IAuditLog audit, TimeProvider time, ILogger<RunSynthesizer> log)
{
    public async Task<RunSynthesis?> SynthesizeAsync(AgentRun run, string? model, CancellationToken ct)
    {
        if (run.Iterations.All(i => !i.Analysis.Parsed)) return null; // nothing to synthesize from
        try
        {
            var (client, _) = llm.Create(model);
            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, Prompts.SynthesisSystem(run.Task)),
                    new ChatMessage(ChatRole.User, Prompts.SynthesisUser(run)),
                ],
                new ChatOptions { Temperature = 0.1f },
                ct);
            var synthesis = AnalysisParser.ParseSynthesis(response.Text);
            await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), run.Task.Id, run.Iterations.Count, "run.synthesized", new
            {
                parsed = synthesis is not null,
                rootCause = synthesis?.RootCause,
                confidence = synthesis?.Confidence,
                inputTokens = response.Usage?.InputTokenCount,
                outputTokens = response.Usage?.OutputTokenCount,
            }), ct);
            return synthesis;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Synthesis of run {RunId} failed; the report falls back to the last iteration", run.Task.Id);
            await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), run.Task.Id, run.Iterations.Count, "run.synthesis_failed",
                new { error = $"{ex.GetType().Name}: {ex.Message}" }), CancellationToken.None);
            return null;
        }
    }
}
