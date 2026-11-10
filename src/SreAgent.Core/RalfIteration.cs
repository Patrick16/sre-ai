using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace SreAgent.Core;

/// <summary>
/// One iteration of the Ralph loop: Retrieve -> Analyze -> Lead -> Feedback.
/// R and F are deterministic code; A is the model (via Microsoft Agent Framework) with read-only tools;
/// L hands the model's proposal (if any) to <see cref="ActionLead"/>: guardrails decide, the model never acts itself.
/// Without an ActionLead (no actions configured) the agent only observes, as in Phase 7.
/// </summary>
public sealed class RalfIteration(
    IObservationCollector collector,
    IChatClientFactory llm,
    IAgentToolset toolset,
    IAuditLog audit,
    ILoggerFactory loggerFactory,
    TimeProvider time,
    ActionLead? lead = null)
{
    public const string ObserveOnlyLead = "observe-only: no actions are configured";
    public const string NoProposal = "no action proposed";

    public async Task<IterationResult> RunAsync(TaskSpec task, RunState state, int number, string? model, CancellationToken ct)
    {
        var started = time.GetUtcNow();
        var startedTs = time.GetTimestamp();

        // R - Retrieve: the base context is gathered by code, so the model doesn't spend steps on the obvious.
        var observations = await collector.CollectAsync(task, ct);
        await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, number, "observation.collected", new { chars = observations.Length }), ct);

        // A - Analyze: a fresh agent session every iteration (the Ralph loop's "fresh context").
        var calls = new List<ToolCallRecord>();
        var budget = new ToolBudget(task.Limits.MaxToolCallsPerIteration);
        var tools = toolset.Tools
            .Select(t => (AITool)new GuardedTool(t, task.Id, number, task.Limits.MaxToolOutputChars, budget, calls, audit, time))
            .ToList();

        var (client, modelId) = llm.Create(model);
        var pipeline = client.AsBuilder()
            .UseFunctionInvocation(loggerFactory, f =>
            {
                // Framework-level safety net per request; the real per-iteration budget is ToolBudget in GuardedTool.
                f.MaximumIterationsPerRequest = task.Limits.MaxToolCallsPerIteration + 1;
                f.MaximumConsecutiveErrorsPerRequest = 2;
            })
            .Build();

        var agent = pipeline.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "sre-agent",
            UseProvidedChatClientAsIs = true, // our pipeline above already does function invocation, with our limits
            ChatOptions = new ChatOptions
            {
                Instructions = Prompts.System(task, lead?.Settings),
                Tools = tools,
                Temperature = 0.1f,
            },
        }, loggerFactory);

        var session = await agent.CreateSessionAsync(ct);
        var response = await agent.RunAsync(Prompts.UserMessage(task, state, observations, number), session, cancellationToken: ct);
        long inTokens = response.Usage?.InputTokenCount ?? 0, outTokens = response.Usage?.OutputTokenCount ?? 0;
        var analysis = AnalysisParser.Parse(response.Text);

        if (!analysis.Parsed)
        {
            // One corrective turn in the same session, tools disabled. Then accept whatever we have.
            var fix = await agent.RunAsync(Prompts.FixFormat, session,
                new ChatClientAgentRunOptions { ChatOptions = new ChatOptions { ToolMode = ChatToolMode.None } }, ct);
            inTokens += fix.Usage?.InputTokenCount ?? 0;
            outTokens += fix.Usage?.OutputTokenCount ?? 0;
            var retried = AnalysisParser.Parse(fix.Text);
            if (retried.Parsed) analysis = retried;
        }

        await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, number, "analysis.completed", new
        {
            model = modelId,
            parsed = analysis.Parsed,
            analysis.Hypothesis,
            analysis.Confidence,
            toolCalls = calls.Count,
            inputTokens = inTokens,
            outputTokens = outTokens,
        }), ct);

        // L - Lead: the proposal goes through the guardrails (and, if executed, its effect is measured - part of F).
        // The rest of F (completion check, RunState) is done by the Ralph loop.
        ActionRecord? action = null;
        var leadText = lead is null ? ObserveOnlyLead : NoProposal;
        if (lead is not null && analysis.Action is { } proposal)
        {
            try
            {
                action = await lead.HandleAsync(proposal, task, state, number, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failure in the guardrails/Lead (API error, disk) must not throw away this iteration's analysis
                // and facts: it becomes a Failed action record, and nothing was executed.
                await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, number, "action.lead_failed",
                    new { proposal.Type, proposal.Target, error = $"{ex.GetType().Name}: {ex.Message}" }), CancellationToken.None);
                action = new ActionRecord(number, time.GetUtcNow(), proposal, lead.Settings.Mode, ActionStatus.Failed,
                    $"could not process the proposal: {ex.GetType().Name}: {ex.Message}", []);
            }
            leadText = $"{proposal.Type} {proposal.Target}: {action.Status} ({action.Mode})" +
                       (action.Executed ? $", effect {action.Effect}" : "") + $" - {action.Detail}";
        }

        return new IterationResult(number, started, time.GetElapsedTime(startedTs), modelId, observations, analysis,
            calls, leadText, new CompletionStatus(false, "not checked yet"), inTokens, outTokens)
        { Action = action };
    }
}
