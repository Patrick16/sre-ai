using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SreAgent.Core;

namespace SreAgent.Core.Tests;

public class SynthesisTests
{
    private static RalphLoop Build(IChatClient client, ICompletionCheck completion, MemoryAudit audit)
    {
        var factory = new FixedFactory(client);
        var iteration = new RalfIteration(new FakeCollector(), factory, new FakeToolset(), audit, NullLoggerFactory.Instance, TimeProvider.System);
        var synthesizer = new RunSynthesizer(factory, audit, TimeProvider.System, NullLogger<RunSynthesizer>.Instance);
        return new RalphLoop(iteration, completion, new MemoryStateStore(), synthesizer, audit, TimeProvider.System, NullLogger<RalphLoop>.Instance);
    }

    [Fact]
    public async Task Facts_verified_in_one_iteration_reach_the_next_fresh_context()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("db down", fact: "StatefulSet postgres desired=0 (describe_workload)")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("pools are stale")),
            ScriptedChatClient.Final(ScriptedChatClient.SynthesisJson("postgres scaled to 0")));
        var loop = Build(model, new ScriptedCompletion(false, true), new MemoryAudit());

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var iteration2Prompt = string.Join("\n", model.Requests[1].Select(m => m.Text));
        Assert.Contains("Established facts", iteration2Prompt);
        Assert.Contains("desired=0", iteration2Prompt);
        Assert.Single(run.Facts);
    }

    [Fact]
    public async Task The_synthesis_sees_every_iteration_and_becomes_the_runs_conclusion()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("statefulset scaled to 0")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("liveness probe failure")),
            ScriptedChatClient.Final(ScriptedChatClient.SynthesisJson("postgres scaled to 0")));
        var audit = new MemoryAudit();
        var loop = Build(model, new ScriptedCompletion(false, true), audit);

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var synthesisPrompt = string.Join("\n", model.Requests[2].Select(m => m.Text));
        Assert.Contains("statefulset scaled to 0", synthesisPrompt);     // early iteration included...
        Assert.Contains("liveness probe failure", synthesisPrompt);      // ...and the last one
        Assert.Null(model.Options[2]?.Tools);                            // no tools: it only reasons over the record
        Assert.Equal("postgres scaled to 0", run.Synthesis?.RootCause);
        Assert.Contains(audit.Events, e => e.Event == "run.synthesized");
    }

    [Fact]
    public async Task A_failed_synthesis_leaves_the_run_intact()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("db down")),
            _ => throw new HttpRequestException("model unloaded"));
        var audit = new MemoryAudit();
        var loop = Build(model, new ScriptedCompletion(true), audit);

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.Null(run.Synthesis);
        Assert.Contains(audit.Events, e => e.Event == "run.synthesis_failed");
    }

    [Fact]
    public void Incident_time_facts_are_never_pushed_out_by_later_ones()
    {
        var state = new RunState { RunId = "r" };
        for (var i = 1; i <= 12; i++)
        {
            var facts = Enumerable.Range(1, 8).Select(n => $"iteration {i} fact {n}").ToList();
            var analysis = new Analysis("s", "h", "low", [], [], [], true, "") { Facts = facts };
            state.Append(new IterationResult(i, DateTimeOffset.UtcNow, TimeSpan.Zero, "m", "", analysis, [], "", new CompletionStatus(false, "x"), 0, 0));
        }

        Assert.Equal(RunState.MaxFacts, state.Facts.Count);
        Assert.Contains(state.Facts, f => f.Text == "iteration 1 fact 1");            // the first (incident-time) facts survive
        Assert.DoesNotContain(state.Facts, f => f.Text == "iteration 1 fact 6");       // at most 5 per iteration
    }

    [Fact]
    public void Synthesis_and_fact_shapes_are_parsed_leniently()
    {
        var s = AnalysisParser.ParseSynthesis("```json\n" + ScriptedChatClient.SynthesisJson("x") + "\n```");
        Assert.NotNull(s);
        Assert.Equal("x", s!.RootCause);
        Assert.Null(AnalysisParser.ParseSynthesis("no json here"));

        var a = AnalysisParser.Parse("""{"hypothesis":"h","facts":[{"fact":"pg_up=0","evidence":"query_metrics"},"plain fact"]}""");
        Assert.Equal(["pg_up=0 (query_metrics)", "plain fact"], a.Facts);
    }
}
