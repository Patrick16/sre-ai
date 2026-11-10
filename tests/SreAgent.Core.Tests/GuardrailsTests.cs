using SreAgent.Core;

namespace SreAgent.Core.Tests;

/// <summary>docs/06: per layer, "allows the right thing" and "refuses the wrong thing".</summary>
public class GuardrailsTests
{
    private static readonly ActionProposal Restart = new(ActionCatalog.RestartDeployment, "orders-api", "stale pool");

    private static Guardrails Build(ActionSettings? settings = null, FakeExecutor? executor = null, FakeKillSwitch? kill = null, IActionLedger? ledger = null, IHealthProbe? probe = null) =>
        new(settings ?? ActionTest.Settings(), executor ?? new FakeExecutor(), kill ?? new FakeKillSwitch(), ledger ?? new InMemoryActionLedger(),
            probe ?? new ConstantProbe(), TimeProvider.System);

    private static async Task<(bool Allowed, ActionMode Mode, IReadOnlyList<GuardrailCheck> Checks)> Evaluate(Guardrails g, ActionProposal p, RunState? state = null) =>
        await g.EvaluateAsync(p, TestTasks.Create(), state ?? new RunState { RunId = "r" }, CancellationToken.None);

    private static ActionRecord Executed(ActionProposal p, ActionEffect effect, int iteration = 1) =>
        new(iteration, DateTimeOffset.UtcNow, p, ActionMode.Auto, ActionStatus.Executed, "did", [], Effect: effect);

    [Fact]
    public async Task Allows_an_allowlisted_action_that_passes_every_check()
    {
        var (allowed, mode, checks) = await Evaluate(Build(), Restart);
        Assert.True(allowed);
        Assert.Equal(ActionMode.Auto, mode);
        Assert.All(checks, c => Assert.True(c.Passed));
    }

    [Theory]
    [InlineData("scale_deployment", "orders-api", "allowlist")]       // not in the catalog/allowlist
    [InlineData("kubectl", "delete ns sandbox", "allowlist")]
    [InlineData("restart_deployment", "orders-api,frontend", "target")] // two targets
    [InlineData("restart_deployment", "../kube-system/coredns", "target")]
    [InlineData("restart_deployment", "Orders API", "target")]
    public async Task Refuses_unknown_types_and_malformed_targets(string type, string target, string failedCheck)
    {
        var (allowed, _, checks) = await Evaluate(Build(), new ActionProposal(type, target, "injected"));
        Assert.False(allowed);
        Assert.Equal(failedCheck, checks[^1].Name);
    }

    [Fact]
    public async Task Effective_mode_is_the_stricter_of_the_global_ceiling_and_the_action_own_mode()
    {
        var (_, mode, _) = await Evaluate(Build(ActionTest.Settings(mode: ActionMode.DryRun, own: ActionMode.Auto)), Restart);
        Assert.Equal(ActionMode.DryRun, mode);
        (_, mode, _) = await Evaluate(Build(ActionTest.Settings(mode: ActionMode.Auto, own: ActionMode.Approve)), Restart);
        Assert.Equal(ActionMode.Approve, mode);
    }

    [Fact]
    public async Task Kill_switch_and_config_switch_stop_everything()
    {
        var (allowed, _, checks) = await Evaluate(Build(kill: new FakeKillSwitch("ConfigMap exists")), Restart);
        Assert.False(allowed);
        Assert.Equal("kill-switch", checks[^1].Name);

        (allowed, _, checks) = await Evaluate(Build(ActionTest.Settings() with { Enabled = false }), Restart);
        Assert.False(allowed);
        Assert.Equal("enabled", checks[^1].Name);
    }

    [Fact]
    public async Task Run_limit_counts_only_executed_actions()
    {
        var state = new RunState { RunId = "r" };
        state.Actions.Add(new ActionRecord(1, DateTimeOffset.UtcNow, Restart, ActionMode.DryRun, ActionStatus.DryRun, "would", []));
        state.Actions.Add(Executed(new ActionProposal(ActionCatalog.DeletePod, "orders-api-x", "r"), ActionEffect.Improved));
        Assert.True((await Evaluate(Build(ActionTest.Settings() with { MaxPerRun = 2 }), Restart, state)).Allowed);

        state.Actions.Add(Executed(new ActionProposal(ActionCatalog.DeletePod, "orders-api-y", "r"), ActionEffect.Improved));
        var (allowed, _, checks) = await Evaluate(Build(ActionTest.Settings() with { MaxPerRun = 2 }), Restart, state);
        Assert.False(allowed);
        Assert.Equal("run-limit", checks[^1].Name);
    }

    [Fact]
    public async Task Does_not_repeat_an_action_that_did_not_help()
    {
        var state = new RunState { RunId = "r" };
        state.Actions.Add(Executed(Restart, ActionEffect.Unchanged));
        var (allowed, _, checks) = await Evaluate(Build(ActionTest.Settings() with { Cooldown = TimeSpan.Zero }), Restart, state);
        Assert.False(allowed);
        Assert.Equal("no-repeat", checks[^1].Name);
    }

    [Fact]
    public async Task Circuit_breaker_opens_after_repeated_ineffective_actions()
    {
        var state = new RunState { RunId = "r" };
        state.Actions.Add(Executed(new ActionProposal(ActionCatalog.DeletePod, "a", "r"), ActionEffect.Unchanged));
        state.Actions.Add(Executed(new ActionProposal(ActionCatalog.DeletePod, "b", "r"), ActionEffect.Unchanged));
        var (allowed, _, checks) = await Evaluate(Build(ActionTest.Settings() with { MaxPerRun = 5 }), Restart, state);
        Assert.False(allowed);
        Assert.Equal("circuit-breaker", checks[^1].Name);
    }

    [Fact]
    public async Task Rate_limit_and_cooldown_hold_across_runs()
    {
        var ledger = new InMemoryActionLedger();
        await ledger.RecordAsync(DateTimeOffset.UtcNow.AddMinutes(-2), ActionRecord.ActionKey(Restart.Type, Restart.Target), CancellationToken.None);
        var (allowed, _, checks) = await Evaluate(Build(ledger: ledger), Restart);  // a NEW run, empty RunState
        Assert.False(allowed);
        Assert.Equal("cooldown", checks[^1].Name);

        await ledger.RecordAsync(DateTimeOffset.UtcNow, "delete_pod:a", CancellationToken.None);
        await ledger.RecordAsync(DateTimeOffset.UtcNow, "delete_pod:b", CancellationToken.None);
        (allowed, _, checks) = await Evaluate(Build(ledger: ledger), new ActionProposal(ActionCatalog.RestartDeployment, "frontend", "r"));
        Assert.False(allowed);
        Assert.Equal("rate-limit", checks[^1].Name);
    }

    [Fact]
    public async Task Live_preconditions_have_the_last_word()
    {
        var (allowed, _, checks) = await Evaluate(Build(executor: new FakeExecutor("Deployment orders-api is annotated sre-agent/protected=true")), Restart);
        Assert.False(allowed);
        Assert.Equal("preconditions", checks[^1].Name);
        Assert.Contains("protected", checks[^1].Detail);
    }

    [Fact]
    public async Task Refuses_to_act_when_a_fresh_reading_shows_nothing_to_fix()
    {
        // The model's evidence was stale: errors had stopped by themselves a minute ago.
        var (allowed, _, checks) = await Evaluate(Build(probe: new ConstantProbe(0.0, 0)), Restart);
        Assert.False(allowed);
        Assert.Equal("current-symptom", checks[^1].Name);

        // A dry run still goes through: it changes nothing and shows what would have happened.
        (allowed, _, _) = await Evaluate(Build(ActionTest.Settings(mode: ActionMode.DryRun), probe: new ConstantProbe(0.0, 0)), Restart);
        Assert.True(allowed);

        // A not-ready pod is a symptom too.
        (allowed, _, _) = await Evaluate(Build(probe: new ConstantProbe(0.0, 1)), Restart);
        Assert.True(allowed);
    }

    [Theory]
    [InlineData(0.50, 0, 0.002, 0, ActionEffect.Improved)]
    [InlineData(0.50, 0, 0.20, 0, ActionEffect.Improved)]
    [InlineData(0.30, 0, 0.28, 0, ActionEffect.Unchanged)]
    [InlineData(0.00, 0, 0.00, 0, ActionEffect.Unchanged)]
    [InlineData(0.10, 0, 0.30, 0, ActionEffect.Degraded)]
    [InlineData(0.50, 0, 0.00, 1, ActionEffect.Degraded)]     // errors gone but a pod stopped being ready
    [InlineData(0.00, 2, 0.00, 0, ActionEffect.Improved)]
    public void Effect_is_judged_from_before_and_after(double e1, int n1, double e2, int n2, ActionEffect expected)
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Equal(expected, EffectJudge.Judge(new HealthSnapshot(at, e1, n1), new HealthSnapshot(at, e2, n2)));
    }
}

public class EffectJudgeTests
{
    private static HealthSnapshot S(double ratio, int notReady = 0) => new(DateTimeOffset.UtcNow, ratio, notReady);

    [Fact]
    public void Nothing_measured_is_NotMeasured_not_Unchanged() =>
        Assert.Equal(ActionEffect.NotMeasured, EffectJudge.Judge(S(double.NaN), S(double.NaN)));

    [Fact]
    public void A_missing_reading_on_one_side_is_NotMeasured() =>
        Assert.Equal(ActionEffect.NotMeasured, EffectJudge.Judge(S(0.4), S(double.NaN)));

    [Fact]
    public void Pods_can_still_tell_when_the_ratio_is_unknown()
    {
        Assert.Equal(ActionEffect.Improved, EffectJudge.Judge(S(double.NaN, 2), S(double.NaN, 0)));
        Assert.Equal(ActionEffect.Degraded, EffectJudge.Judge(S(double.NaN, 0), S(double.NaN, 1)));
    }

    [Fact]
    public void Measured_readings_judge_as_before()
    {
        Assert.Equal(ActionEffect.Improved, EffectJudge.Judge(S(0.4), S(0.0)));
        Assert.Equal(ActionEffect.Unchanged, EffectJudge.Judge(S(0.4), S(0.4)));
        Assert.Equal(ActionEffect.Degraded, EffectJudge.Judge(S(0.1), S(0.6)));
    }
}
