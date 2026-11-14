using SreAgent.Core;
using SreAgent.Host;

namespace SreAgent.Host.Tests;

/// <summary>The console's backend: the live trace store, the names it shows, and the mode switch's effect.</summary>
public class ConsoleTests
{
    private static IterationResult Iteration(int n, string hypothesis, ActionRecord? action = null) => new(
        n, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), "test-model", "obs",
        new Analysis("summary", hypothesis, "high", ["ev"], [], ["check"], Parsed: true, RawText: "<raw reply>") { Facts = ["fact"] },
        [new ToolCallRecord(n, "get_pods", "{}", "ok", false, TimeSpan.FromMilliseconds(5))],
        "none", new CompletionStatus(n > 1, "why"), 10, 20)
    { Action = action };

    [Fact]
    public void The_trace_keeps_every_finished_iteration_in_order()
    {
        var store = new RunTraceStore();
        var task = TestTask("run-a");

        store.IterationFinished(task, Iteration(1, "pool"));
        store.IterationFinished(task, Iteration(2, "db"));

        var views = store.Get("run-a");
        Assert.Equal([1, 2], views.Select(v => v.Number));
        Assert.Equal("<raw reply>", views[0].Thinking);
        Assert.True(views[1].Done);
        Assert.Empty(store.Get("unknown-run"));
    }

    [Fact]
    public void Old_runs_are_forgotten_after_thirty()
    {
        var store = new RunTraceStore();
        for (var i = 0; i < 31; i++) store.IterationFinished(TestTask($"run-{i}"), Iteration(1, "x"));

        Assert.Empty(store.Get("run-0"));
        Assert.Single(store.Get("run-30"));
    }

    [Fact]
    public void Actions_show_enum_names_not_numbers()
    {
        var proposal = new ActionProposal("restart_deployment", "orders-api", "stale pool");
        var record = new ActionRecord(1, DateTimeOffset.UtcNow, proposal, ActionMode.Auto, ActionStatus.Executed, "done", [],
            Before: new HealthSnapshot(DateTimeOffset.UtcNow, double.NaN, 0), Effect: ActionEffect.Improved);

        var view = IterationView.From(Iteration(1, "pool", record)).Action!;

        Assert.Equal("Auto", view.Mode);
        Assert.Equal("Executed", view.Status);
        Assert.Equal("Improved", view.Effect);
        Assert.Contains("n/a", view.Before);   // NaN is printed as "not measured", never serialized as NaN
    }

    [Fact]
    public void A_runtime_mode_change_is_seen_by_every_later_decision()
    {
        var settings = new ActionSettings
        {
            Mode = ActionMode.DryRun,
            Allowed = new Dictionary<string, ActionMode> { ["restart_deployment"] = ActionMode.Auto, ["delete_pod"] = ActionMode.Approve },
        };
        Assert.Equal(ActionMode.DryRun, settings.EffectiveMode("restart_deployment"));

        settings.Mode = ActionMode.Auto;   // what PUT /actions/mode does

        Assert.Equal(ActionMode.Auto, settings.EffectiveMode("restart_deployment"));
        Assert.Equal(ActionMode.Approve, settings.EffectiveMode("delete_pod"));
        Assert.Equal(ActionMode.DryRun, settings.EffectiveMode("not_allowed"));
    }

    [Fact]
    public void The_console_page_is_embedded_in_the_agent()
    {
        using var stream = typeof(RunTraceStore).Assembly.GetManifestResourceStream("console.html");
        Assert.NotNull(stream);
    }

    private static TaskSpec TestTask(string id) =>
        new(id, "goal", new TriggerEvent(TriggerKind.Manual, [], "test", DateTimeOffset.UtcNow), "sandbox", new AgentLimits(), "English");
}
