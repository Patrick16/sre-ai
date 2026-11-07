using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SreAgent.Core;

namespace SreAgent.Core.Tests;

public class RalphLoopTests
{
    private static (RalphLoop Loop, MemoryAudit Audit, MemoryStateStore States, FakeCollector Collector) Build(
        IChatClient client, ICompletionCheck completion, params AIFunction[] tools)
    {
        var audit = new MemoryAudit();
        var states = new MemoryStateStore();
        var collector = new FakeCollector();
        var iteration = new RalfIteration(collector, new FixedFactory(client), new FakeToolset(tools), audit, NullLoggerFactory.Instance, TimeProvider.System);
        var loop = new RalphLoop(iteration, completion, states, audit, TimeProvider.System, NullLogger<RalphLoop>.Instance);
        return (loop, audit, states, collector);
    }

    [Fact]
    public async Task Stops_as_soon_as_the_completion_check_says_done()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("postgres is down")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("postgres is back")));
        var (loop, _, states, collector) = Build(model, new ScriptedCompletion(false, true));

        var run = await loop.RunAsync(TestTasks.Create(maxIterations: 5), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.Equal(2, run.Iterations.Count);
        Assert.Equal(2, collector.Calls);            // Retrieve runs every iteration
        Assert.Equal(2, states.Saves);               // RunState persisted after every iteration
        Assert.Equal("postgres is back", run.Iterations[^1].Analysis.Hypothesis);
    }

    [Fact]
    public async Task Gives_up_after_max_iterations_even_if_the_model_is_confident()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("fixed", "high")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("fixed", "high")));
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(false, false));

        var run = await loop.RunAsync(TestTasks.Create(maxIterations: 2), null, CancellationToken.None);

        Assert.Equal(RunOutcome.LimitsExhausted, run.Outcome);
        Assert.Equal(2, run.Iterations.Count);
    }

    [Fact]
    public async Task Every_iteration_starts_with_a_fresh_context_carrying_only_the_run_state()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.CallTool("list_pods"),
            ScriptedChatClient.Final(ScriptedChatClient.Json("first-hypothesis")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("second-hypothesis")));
        var tool = AIFunctionFactory.Create(() => "SECRET-TOOL-OUTPUT-FROM-ITERATION-1", "list_pods");
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(false, true), tool);

        await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        var iteration2FirstRequest = model.Requests[2];
        var text = string.Join("\n", iteration2FirstRequest.Select(m => m.Text));
        Assert.Single(iteration2FirstRequest, m => m.Role == ChatRole.User);               // no old conversation
        Assert.Contains("first-hypothesis", text);                                        // memory via RunState...
        Assert.DoesNotContain("SECRET-TOOL-OUTPUT-FROM-ITERATION-1", text);               // ...not via chat history
    }

    [Fact]
    public async Task Tool_output_is_truncated_to_the_budget_and_recorded()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.CallTool("query_logs"),
            ScriptedChatClient.Final(ScriptedChatClient.Json("noisy logs")));
        var tool = AIFunctionFactory.Create(() => new string('x', 10_000), "query_logs");
        var (loop, audit, _, _) = Build(model, new ScriptedCompletion(true), tool);

        var run = await loop.RunAsync(TestTasks.Create(maxOutputChars: 500), null, CancellationToken.None);

        var resultSentToModel = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.True(resultSentToModel.Result!.ToString()!.Length < 700);
        Assert.Contains("truncated", resultSentToModel.Result!.ToString());
        Assert.Single(run.Iterations[0].ToolCalls);
        Assert.Contains(audit.Events, e => e.Event == "tool.called");
    }

    [Fact]
    public async Task A_failing_tool_becomes_an_error_message_for_the_model_not_a_crash()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.CallTool("query_metrics"),
            ScriptedChatClient.Final(ScriptedChatClient.Json("prometheus unreachable")));
        var tool = AIFunctionFactory.Create(string () => throw new HttpRequestException("connection refused"), "query_metrics");
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(true), tool);

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.True(run.Iterations[0].ToolCalls[0].Failed);
        Assert.Contains("connection refused", run.Iterations[0].ToolCalls[0].ResultPreview);
    }

    [Fact]
    public async Task A_model_that_never_stops_calling_tools_is_cut_off_by_the_step_budget()
    {
        var script = Enumerable.Range(0, 10).Select(_ => ScriptedChatClient.CallTool("list_pods"))
            .Append(ScriptedChatClient.Final(ScriptedChatClient.Json("answered after being stopped")))
            .ToArray();
        var model = new ScriptedChatClient(script);
        var tool = AIFunctionFactory.Create(() => "pods", "list_pods");
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(true), tool);

        var run = await loop.RunAsync(TestTasks.Create(maxToolCalls: 3), null, CancellationToken.None);

        Assert.True(run.Iterations[0].ToolCalls.Count <= 3);
        Assert.Equal(RunOutcome.Resolved, run.Outcome);
    }

    [Fact]
    public async Task An_unstructured_answer_gets_one_corrective_turn_without_tools()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final("I think the database is down."),
            ScriptedChatClient.Final(ScriptedChatClient.Json("database down")));
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(true));

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.True(run.Iterations[0].Analysis.Parsed);
        Assert.Equal("database down", run.Iterations[0].Analysis.Hypothesis);
        Assert.Equal(ChatToolMode.None, model.Options[1]?.ToolMode);
    }

    [Fact]
    public async Task A_model_that_keeps_failing_ends_the_run_as_failed_instead_of_throwing()
    {
        var model = new ScriptedChatClient(
            _ => throw new HttpRequestException("LM Studio is not running"),
            _ => throw new HttpRequestException("LM Studio is not running"));
        var (loop, audit, _, _) = Build(model, new ScriptedCompletion(false, false, false));

        var run = await loop.RunAsync(TestTasks.Create(maxIterations: 5), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Equal(2, run.Iterations.Count);                         // MaxConsecutiveFailures = 2
        Assert.Contains("LM Studio", run.OutcomeReason);
        Assert.Contains(audit.Events, e => e.Event == "iteration.failed");
        Assert.Contains(audit.Events, e => e.Event == "run.finished");
    }

    [Fact]
    public async Task One_model_glitch_does_not_stop_the_watch()
    {
        var model = new ScriptedChatClient(
            _ => throw new HttpRequestException("model was being reloaded"),
            ScriptedChatClient.Final(ScriptedChatClient.Json("postgres is down")));
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(false, true));

        var run = await loop.RunAsync(TestTasks.Create(maxIterations: 5), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.False(run.Iterations[0].Analysis.Parsed);
        Assert.Equal("postgres is down", run.Iterations[1].Analysis.Hypothesis);
    }

    [Fact]
    public async Task The_completion_check_still_runs_when_the_model_fails()
    {
        var model = new ScriptedChatClient(_ => throw new HttpRequestException("timeout"));
        var (loop, _, _, _) = Build(model, new ScriptedCompletion(true));

        var run = await loop.RunAsync(TestTasks.Create(), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);   // the cluster recovered; that's what counts
    }
}
