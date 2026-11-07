using Microsoft.Extensions.Logging;

namespace SreAgent.Core;

/// <summary>
/// The outer loop: run the same task again and again - each time with a fresh model context and the persisted
/// RunState as memory - until a deterministic completion check says it is done or a limit is reached.
/// </summary>
public sealed class RalphLoop(
    RalfIteration iteration,
    ICompletionCheck completion,
    IRunStateStore states,
    RunSynthesizer synthesizer,
    IAuditLog audit,
    TimeProvider time,
    ILogger<RalphLoop> log)
{
    public async Task<AgentRun> RunAsync(TaskSpec task, string? model, CancellationToken ct)
    {
        var run = new AgentRun(task, time.GetUtcNow());
        var deadline = run.StartedAt + task.Limits.MaxWallTime;
        var state = await states.LoadAsync(task.Id, ct) ?? new RunState { RunId = task.Id };
        await audit.WriteAsync(new AuditEvent(run.StartedAt, task.Id, 0, "run.started",
            new { trigger = task.Trigger.Kind.ToString(), alerts = task.Trigger.Alerts.Select(a => a.Name), task.Goal }), ct);

        var consecutiveFailures = 0;
        try
        {
            for (var i = state.History.Count + 1; i <= task.Limits.MaxIterations; i++)
            {
                if (time.GetUtcNow() >= deadline)
                {
                    Finish(run, RunOutcome.LimitsExhausted, $"wall time limit {task.Limits.MaxWallTime} reached");
                    break;
                }

                await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, i, "iteration.started", null), ct);
                IterationResult result;
                try
                {
                    result = await iteration.RunAsync(task, state, i, model, ct);
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // The model (or a dependency) failed this iteration. Record it and keep watching: the completion
                    // check below doesn't need the model, and the next iteration may well work again.
                    consecutiveFailures++;
                    log.LogWarning(ex, "Run {RunId} iteration {Iteration} failed ({Count} in a row)", task.Id, i, consecutiveFailures);
                    await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, i, "iteration.failed",
                        new { error = $"{ex.GetType().Name}: {ex.Message}", consecutiveFailures }), ct);
                    result = FailedIteration(i, ex);
                }

                // F - Feedback: "done" is decided here, by code, from the cluster's state - not by the model.
                var status = await completion.CheckAsync(task, i, ct);
                result = result with { Completion = status };
                run.Iterations.Add(result);
                state.Append(result);
                await states.SaveAsync(state, ct); // persisted after every iteration: a restart loses at most one
                await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, i, "completion.checked", new { status.Done, status.Reason }), ct);
                log.LogInformation("Run {RunId} iteration {Iteration}: {Hypothesis} (done={Done}: {Reason})",
                    task.Id, i, result.Analysis.Hypothesis, status.Done, status.Reason);

                if (status.Done)
                {
                    Finish(run, RunOutcome.Resolved, status.Reason);
                    break;
                }
                if (consecutiveFailures >= task.Limits.MaxConsecutiveFailures)
                {
                    Finish(run, RunOutcome.Failed, $"{consecutiveFailures} iterations in a row failed; last error: {result.Analysis.Summary}");
                    break;
                }
                if (i < task.Limits.MaxIterations)
                    await Task.Delay(task.Limits.Backoff, time, ct); // give the system time to change before looking again
            }

            if (run.Outcome is null)
                Finish(run, RunOutcome.LimitsExhausted, $"{task.Limits.MaxIterations} iterations without the task being done - a human should look");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(run, RunOutcome.Cancelled, "the agent is shutting down");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Run {RunId} failed", task.Id);
            Finish(run, RunOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }

        run.Facts = state.Facts.Select(f => $"[{f.At:HH:mm:ss}Z, iteration {f.Iteration}] {f.Text}").ToList();
        if (run.Outcome != RunOutcome.Cancelled)
            run.Synthesis = await synthesizer.SynthesizeAsync(run, model, ct);

        await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, run.Iterations.Count, "run.finished",
            new { outcome = run.Outcome.ToString(), reason = run.OutcomeReason }), CancellationToken.None);
        return run;
    }

    private IterationResult FailedIteration(int number, Exception ex)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";
        var analysis = new Analysis(error, "(iteration failed - no analysis)", "low", [], [], [], Parsed: false, RawText: "");
        return new IterationResult(number, time.GetUtcNow(), TimeSpan.Zero, "-", "", analysis, [], "none: iteration failed",
            new CompletionStatus(false, "not checked yet"), 0, 0);
    }

    private void Finish(AgentRun run, RunOutcome outcome, string reason)
    {
        run.Outcome = outcome;
        run.OutcomeReason = reason;
        run.FinishedAt = time.GetUtcNow();
    }
}
