using Microsoft.Extensions.Logging;

namespace SreAgent.Core;

/// <summary>
/// L (Lead) and the action part of F (Feedback) of a RALF iteration:
///   proposal -> guardrails -> mode:  DryRun  -> server-side dry run, report "would do X"
///                                    Approve -> wait for a human, then as Auto (or Rejected/Expired)
///                                    Auto    -> snapshot, execute, wait, snapshot, judge the effect
/// Every step is audited before it happens and after.
/// </summary>
public sealed class ActionLead(Guardrails guardrails, IActionExecutor executor, IHealthProbe probe, IApprovalGate approvals, IAuditLog audit, TimeProvider time, ILogger<ActionLead> log)
{
    public ActionSettings Settings => guardrails.Settings;

    public async Task<ActionRecord> HandleAsync(ActionProposal proposal, TaskSpec task, RunState state, int iteration, CancellationToken ct)
    {
        var at = time.GetUtcNow();
        await Audit(task, iteration, "action.proposed", new { proposal.Type, proposal.Target, proposal.Reason }, ct);

        var (allowed, mode, checks) = await guardrails.EvaluateAsync(proposal, task, state, ct);
        await Audit(task, iteration, "action.guardrails", new { allowed, mode = mode.ToString(), checks }, ct);
        if (!allowed)
        {
            var failed = checks.Last();
            return new ActionRecord(iteration, at, proposal, mode, ActionStatus.Refused, $"{failed.Name}: {failed.Detail}", checks);
        }

        if (mode == ActionMode.DryRun)
            return await DryRunAsync(proposal, task, iteration, at, mode, checks, ct);

        string? approvedBy = null;
        if (mode == ActionMode.Approve)
        {
            var request = new ApprovalRequest($"{task.Id}-{iteration}", task.Id, iteration, task.Namespace, proposal, time.GetUtcNow() + Settings.ApprovalTimeout);
            await Audit(task, iteration, "action.approval_requested", new { request.Id, request.ExpiresAt }, ct);
            var decision = await approvals.RequestAsync(request, ct);
            await Audit(task, iteration, "action.approval_decided", new { request.Id, decision.Approved, decision.By, decision.Expired }, ct);
            if (!decision.Approved)
                return new ActionRecord(iteration, at, proposal, mode,
                    decision.Expired ? ActionStatus.Expired : ActionStatus.Rejected,
                    decision.Expired ? $"no decision within {Settings.ApprovalTimeout.TotalMinutes:0} min" : $"rejected by {decision.By}", checks);
            approvedBy = decision.By;
            // The world may have changed while a human was thinking: check again right before acting (incl. the symptom).
            var (stillAllowed, _, recheck) = await guardrails.EvaluateAsync(proposal, task, state, ct);
            if (!stillAllowed)
                return new ActionRecord(iteration, at, proposal, mode, ActionStatus.Refused, $"after approval: {recheck.Last().Name}: {recheck.Last().Detail}", recheck, ApprovedBy: approvedBy);
        }

        return await ExecuteAsync(proposal, task, iteration, at, mode, checks, approvedBy, ct);
    }

    private async Task<ActionRecord> DryRunAsync(ActionProposal p, TaskSpec task, int iteration, DateTimeOffset at, ActionMode mode, IReadOnlyList<GuardrailCheck> checks, CancellationToken ct)
    {
        try
        {
            var detail = await executor.ExecuteAsync(p, task.Namespace, dryRun: true, ct);
            await Audit(task, iteration, "action.dry_run", new { p.Type, p.Target, detail }, ct);
            return new ActionRecord(iteration, at, p, mode, ActionStatus.DryRun, detail, checks);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dry run that the API server refuses (e.g. RBAC 403) is valuable information, not a crash.
            await Audit(task, iteration, "action.dry_run_failed", new { p.Type, p.Target, error = ex.Message }, ct);
            return new ActionRecord(iteration, at, p, mode, ActionStatus.Failed, $"dry run refused by the API server: {ex.Message}", checks);
        }
    }

    private async Task<ActionRecord> ExecuteAsync(ActionProposal p, TaskSpec task, int iteration, DateTimeOffset at, ActionMode mode,
        IReadOnlyList<GuardrailCheck> checks, string? approvedBy, CancellationToken ct)
    {
        var before = await probe.SnapshotAsync(task.Namespace, ct);
        string detail;
        try
        {
            await Audit(task, iteration, "action.executing", new { p.Type, p.Target, mode = mode.ToString(), approvedBy, before }, ct);
            detail = await executor.ExecuteAsync(p, task.Namespace, dryRun: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Action {Type} {Target} failed", p.Type, p.Target);
            await Audit(task, iteration, "action.failed", new { p.Type, p.Target, error = ex.Message }, ct);
            return new ActionRecord(iteration, at, p, mode, ActionStatus.Failed, ex.Message, checks, before, ApprovedBy: approvedBy);
        }
        // Recorded right away: if the agent dies while waiting, the rate limit still counts this action.
        try
        {
            await guardrails.RecordExecutedAsync(time.GetUtcNow(), ActionRecord.ActionKey(p.Type, p.Target), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The action already happened; a full disk must not make the run forget it (RunState still has it).
            log.LogError(ex, "Could not record the executed action {Type} {Target} in the ledger", p.Type, p.Target);
        }
        log.LogWarning("Action executed: {Type} {Target} ({Detail})", p.Type, p.Target, detail);

        // F - did it help? Wait for the rollout and the metric windows, then compare.
        // The action HAS happened. Whatever goes wrong from here (Prometheus down, agent shutting down) must not
        // erase it from RunState and the report, so the verification is best-effort: effect = NotMeasured.
        HealthSnapshot? after = null;
        var effect = ActionEffect.NotMeasured;
        try
        {
            await Task.Delay(Settings.VerifyDelay, time, ct);
            after = await probe.SnapshotAsync(task.Namespace, ct);
            effect = EffectJudge.Judge(before, after);
            await Audit(task, iteration, "action.verified", new { p.Type, p.Target, before, after, effect = effect.ToString() }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Action {Type} {Target} was executed but its effect could not be verified", p.Type, p.Target);
            try { await Audit(task, iteration, "action.verify_failed", new { p.Type, p.Target, error = $"{ex.GetType().Name}: {ex.Message}" }, CancellationToken.None); }
            catch (Exception auditEx) { log.LogError(auditEx, "Could not audit the failed verification"); }
            detail += $" (effect could not be verified: {ex.GetType().Name}: {ex.Message})";
        }
        return new ActionRecord(iteration, at, p, mode, ActionStatus.Executed, detail, checks, before, after, effect, approvedBy);
    }

    private Task Audit(TaskSpec task, int iteration, string name, object data, CancellationToken ct) =>
        audit.WriteAsync(new AuditEvent(time.GetUtcNow(), task.Id, iteration, name, data), ct);
}
