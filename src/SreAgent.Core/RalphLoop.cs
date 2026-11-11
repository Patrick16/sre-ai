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
    ILogger<RalphLoop> log,
    IRunObserver? observer = null)
{
    private readonly IRunObserver _observer = observer ?? NullRunObserver.Instance;

    public async Task<AgentRun> RunAsync(TaskSpec task, string? model, CancellationToken ct)
    {
        var run = new AgentRun(task, time.GetUtcNow());
        var deadline = run.StartedAt + task.Limits.MaxWallTime;
        var state = new RunState { RunId = task.Id };

        var consecutiveFailures = 0;
        try
        {
            // Inside the try: a disk/PVC error here must end THIS run as Failed, not crash the worker.
            // NOTE: task.Id is new for every run, so this finds nothing today - a run is NOT resumed after an agent
            // restart (RunState is a journal; docs/16). The call is the hook for real resumption with a stable id.
            state = await states.LoadAsync(task.Id, ct) ?? state;
            await audit.WriteAsync(new AuditEvent(run.StartedAt, task.Id, 0, "run.started",
                new { trigger = task.Trigger.Kind.ToString(), alerts = task.Trigger.Alerts.Select(a => a.Name), task.Goal }), ct);

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
                CompletionStatus status;
                try
                {
                    status = await completion.CheckAsync(task, i, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // A blip in Prometheus/Alertmanager must not end the run (and must not lose this iteration's
                    // result, e.g. an executed action): "cannot check" means "not done yet", try again next time.
                    log.LogWarning(ex, "Run {RunId} iteration {Iteration}: the completion check failed", task.Id, i);
                    status = new CompletionStatus(false, $"cannot check: {ex.GetType().Name}: {ex.Message}");
                }
                result = result with { Completion = status };
                run.Iterations.Add(result);
                state.Append(result);
                await states.SaveAsync(state, ct); // persisted after every iteration: a restart loses at most one
                await audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, i, "completion.checked", new { status.Done, status.Reason }), ct);
                try
                {
                    _observer.IterationFinished(task, result);
                }
                catch (Exception ex)
                {
                    // A live view must never change the outcome of a run.
                    log.LogWarning(ex, "Run {RunId}: the run observer failed on iteration {Iteration}", task.Id, i);
                }
                log.LogInformation("Run {RunId} iteration {Iteration}: {Hypothesis} (done={Done}: {Reason})",
                    task.Id, i, result.Analysis.Hypothesis, status.Done, status.Reason);

                if (status.Done)
                {
                    Finish(run, RunOutcome.Resolved, status.Reason);
                    break;
                }
                if (result.Action is { Effect: ActionEffect.Degraded } bad)
                {
                    // The agent made things worse: no further action, no further attempts - a human takes over.
                    Finish(run, RunOutcome.Escalated,
                        $"action {bad.Proposal.Type} {bad.Proposal.Target} made things worse (before: {bad.Before}; after: {bad.After}) - stopped, a human must take over");
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
            // Never overwrite an outcome the loop has already decided (e.g. Resolved).
            if (run.Outcome is null) Finish(run, RunOutcome.Cancelled, "the agent is shutting down");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Run {RunId} failed", task.Id);
            if (run.Outcome is null) Finish(run, RunOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }

        run.Facts = state.Facts.Select(f => $"[{f.At:HH:mm:ss}Z, iteration {f.Iteration}] {f.Text}").ToList();
        if (run.Outcome != RunOutcome.Cancelled)
        {
            try
            {
                run.Synthesis = await synthesizer.SynthesizeAsync(run, model, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down during the synthesis: keep the outcome (e.g. Resolved) and the iterations, skip the summary
                // (the report falls back to the last iteration).
                log.LogWarning("Run {RunId}: the synthesis was cancelled by shutdown", task.Id);
            }
        }

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
