using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SreAgent.Core;

namespace SreAgent.Core.Tests;

/// <summary>The Lead step inside the Ralph loop: modes, effect verification, escalation, memory of actions.</summary>
public class ActionLoopTests
{
    private sealed record Harness(RalphLoop Loop, FakeExecutor Executor, FakeApprovals Approvals, MemoryAudit Audit, ScriptedChatClient Model);

    private static Harness Build(ScriptedChatClient model, ICompletionCheck completion, ActionSettings settings,
        IHealthProbe? probe = null, ApprovalDecision? decision = null, FakeExecutor? executor = null)
    {
        var audit = new MemoryAudit();
        executor ??= new FakeExecutor();
        var approvals = new FakeApprovals(decision ?? new ApprovalDecision(true, "alice"));
        var guardrails = new Guardrails(settings, executor, new FakeKillSwitch(), new InMemoryActionLedger(), new ConstantProbe(), TimeProvider.System);
        var lead = new ActionLead(guardrails, executor, probe ?? new FakeProbe((0.5, 0), (0.0, 0)), approvals, audit, TimeProvider.System, NullLogger<ActionLead>.Instance);
        var factory = new FixedFactory(model);
        var iteration = new RalfIteration(new FakeCollector(), factory, new FakeToolset(), audit, NullLoggerFactory.Instance, TimeProvider.System, lead);
        var synthesizer = new RunSynthesizer(factory, audit, TimeProvider.System, NullLogger<RunSynthesizer>.Instance);
        var loop = new RalphLoop(iteration, completion, new MemoryStateStore(), synthesizer, audit, TimeProvider.System, NullLogger<RalphLoop>.Instance);
        return new Harness(loop, executor, approvals, audit, model);
    }

    private static ScriptedChatClient Model(params string[] answers) =>
        new([.. answers.Select(ScriptedChatClient.Final), ScriptedChatClient.Final(ScriptedChatClient.SynthesisJson("stale pool"))]);

    private static readonly string ProposeRestart = ActionTest.JsonWithAction("stale connection pool", ActionCatalog.RestartDeployment, "orders-api");

    [Fact]
    public async Task Dry_run_mode_only_asks_the_api_server_and_changes_nothing()
    {
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(mode: ActionMode.DryRun));

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var action = Assert.Single(run.Actions);
        Assert.Equal(ActionStatus.DryRun, action.Status);
        Assert.All(h.Executor.Calls, c => Assert.True(c.DryRun));
        Assert.Contains(h.Audit.Events, e => e.Event == "action.dry_run");
    }

    [Fact]
    public async Task A_runs_own_mode_replaces_the_global_ceiling_for_that_run_only()
    {
        // Global ceiling DryRun (as in the values file); the human who started this run chose Auto.
        var settings = ActionTest.Settings(mode: ActionMode.DryRun);
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), settings, new FakeProbe((0.4, 0), (0.0, 0)));

        var run = await h.Loop.RunAsync(TestTasks.Create() with { Mode = ActionMode.Auto }, null, CancellationToken.None);

        var action = Assert.Single(run.Actions);
        Assert.Equal(ActionStatus.Executed, action.Status);
        Assert.Equal(ActionMode.Auto, action.Mode);
        Assert.Equal(ActionMode.DryRun, settings.Mode);   // the global ceiling is untouched
    }

    [Fact]
    public async Task A_runs_mode_cannot_lift_an_actions_own_mode()
    {
        // delete_pod-style setup: the action itself is Approve-only; a run started as Auto still needs the human.
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(mode: ActionMode.DryRun, own: ActionMode.Approve),
            decision: new ApprovalDecision(false, "bob"));

        var run = await h.Loop.RunAsync(TestTasks.Create() with { Mode = ActionMode.Auto }, null, CancellationToken.None);

        var action = Assert.Single(run.Actions);
        Assert.Equal(ActionMode.Approve, action.Mode);
        Assert.Equal(ActionStatus.Rejected, action.Status);
    }

    [Fact]
    public async Task Auto_mode_executes_and_measures_the_effect()
    {
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(), new FakeProbe((0.4, 0), (0.0, 0)));

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var action = Assert.Single(run.Actions);
        Assert.Equal(ActionStatus.Executed, action.Status);
        Assert.Equal(ActionEffect.Improved, action.Effect);
        Assert.Equal(0.4, action.Before!.ErrorRatio);
        Assert.Single(h.Executor.Calls, c => !c.DryRun);
        // audited before AND after execution
        var events = h.Audit.Events.Select(e => e.Event).ToList();
        Assert.True(events.IndexOf("action.executing") < events.IndexOf("action.verified"));
    }

    [Fact]
    public async Task A_degrading_action_escalates_and_stops_the_run()
    {
        var h = Build(Model(ProposeRestart, ProposeRestart), new ScriptedCompletion(false, false, false),
            ActionTest.Settings(), new FakeProbe((0.1, 0), (0.6, 1)));

        var run = await h.Loop.RunAsync(TestTasks.Create(maxIterations: 3), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Escalated, run.Outcome);
        Assert.Single(run.Iterations);                       // no second attempt
        Assert.Single(h.Executor.Calls);
    }

    [Fact]
    public async Task Approve_mode_waits_for_a_human_and_a_rejection_changes_nothing()
    {
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(own: ActionMode.Approve),
            decision: new ApprovalDecision(false, "bob"));

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Single(h.Approvals.Requests);
        Assert.Equal(ActionStatus.Rejected, Assert.Single(run.Actions).Status);
        Assert.Empty(h.Executor.Calls);
    }

    [Fact]
    public async Task Approved_action_runs_and_records_who_approved_it()
    {
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(own: ActionMode.Approve));

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var action = Assert.Single(run.Actions);
        Assert.Equal(ActionStatus.Executed, action.Status);
        Assert.Equal("alice", action.ApprovedBy);
    }

    [Fact]
    public async Task The_next_fresh_context_knows_what_was_done_and_a_useless_repeat_is_refused()
    {
        // restart doesn't help (unchanged) -> the model proposes it again -> no-repeat refuses, nothing executes twice
        var h = Build(Model(ProposeRestart, ProposeRestart), new ScriptedCompletion(false, true),
            ActionTest.Settings() with { Cooldown = TimeSpan.Zero }, new FakeProbe((0.3, 0), (0.3, 0)));

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal([ActionStatus.Executed, ActionStatus.Refused], run.Actions.Select(a => a.Status));
        Assert.Contains("no-repeat", run.Actions[1].Detail);
        Assert.Single(h.Executor.Calls);
        var iteration2Prompt = string.Join("\n", h.Model.Requests[1].Select(m => m.Text));
        Assert.Contains("restart_deployment orders-api -> Executed, effect Unchanged", iteration2Prompt);
    }

    [Fact]
    public async Task Without_an_action_lead_a_proposal_is_ignored()
    {
        var model = Model(ProposeRestart);
        var factory = new FixedFactory(model);
        var audit = new MemoryAudit();
        var iteration = new RalfIteration(new FakeCollector(), factory, new FakeToolset(), audit, NullLoggerFactory.Instance, TimeProvider.System);
        var loop = new RalphLoop(iteration, new ScriptedCompletion(true), new MemoryStateStore(),
            new RunSynthesizer(factory, audit, TimeProvider.System, NullLogger<RunSynthesizer>.Instance), audit, TimeProvider.System, NullLogger<RalphLoop>.Instance);

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Empty(run.Actions);
        Assert.Equal(RalfIteration.ObserveOnlyLead, run.Iterations[0].Lead);
        Assert.DoesNotContain("\"action\"", model.Options[0]?.Instructions ?? "");   // the model isn't even told actions exist
    }

    [Fact]
    public async Task The_model_is_told_which_actions_exist_and_in_which_mode()
    {
        var h = Build(Model(ScriptedChatClient.Json("fine")), new ScriptedCompletion(true), ActionTest.Settings(mode: ActionMode.Approve));

        await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var instructions = h.Model.Options[0]?.Instructions ?? string.Join("\n", h.Model.Requests[0].Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("restart_deployment (mode Approve)", instructions);
        Assert.Contains("delete_pod (mode Approve)", instructions);
    }

    [Fact]
    public async Task An_executed_action_is_kept_even_if_verifying_its_effect_fails()
    {
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(), new ProbeThatFailsAfterFirst());

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var action = Assert.Single(run.Actions);                    // it happened, so it is in the record (and the report)
        Assert.Equal(ActionStatus.Executed, action.Status);
        Assert.Equal(ActionEffect.NotMeasured, action.Effect);
        Assert.Contains("could not be verified", action.Detail);
        Assert.Contains(h.Audit.Events, e => e.Event == "action.verify_failed");
    }

    [Fact]
    public async Task A_failure_in_the_guardrails_keeps_the_analysis_and_records_a_failed_action()
    {
        var executor = new FakeExecutor { ThrowOnPrecondition = new HttpRequestException("api server 500") };
        var h = Build(Model(ProposeRestart), new ScriptedCompletion(true), ActionTest.Settings(), executor: executor);

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal("stale connection pool", run.Iterations[0].Analysis.Hypothesis);   // not replaced by "iteration failed"
        Assert.Equal(ActionStatus.Failed, Assert.Single(run.Actions).Status);
        Assert.Empty(executor.Calls);                                                    // nothing was sent to the cluster
    }

    [Fact]
    public async Task A_target_name_longer_than_a_kubernetes_name_is_refused()
    {
        var long_ = new string('a', 254);
        var h = Build(Model(ActionTest.JsonWithAction("x", ActionCatalog.RestartDeployment, long_)), new ScriptedCompletion(true), ActionTest.Settings());

        var run = await h.Loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal(ActionStatus.Refused, Assert.Single(run.Actions).Status);
        Assert.Empty(h.Executor.Calls);
    }
}
