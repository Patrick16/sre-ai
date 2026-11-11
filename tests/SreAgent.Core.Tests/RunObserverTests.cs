using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SreAgent.Core;

namespace SreAgent.Core.Tests;

/// <summary>The live view hook: every finished iteration is announced, in order, and a broken observer changes nothing.</summary>
public class RunObserverTests
{
    private sealed class Recorder : IRunObserver
    {
        public List<int> Numbers { get; } = [];
        public void IterationFinished(TaskSpec task, IterationResult result) => Numbers.Add(result.Number);
    }

    private sealed class Throwing : IRunObserver
    {
        public void IterationFinished(TaskSpec task, IterationResult result) => throw new InvalidOperationException("boom");
    }

    private static RalphLoop Loop(IChatClient client, ICompletionCheck completion, IRunObserver? observer)
    {
        var audit = new MemoryAudit();
        var factory = new FixedFactory(client);
        var iteration = new RalfIteration(new FakeCollector(), factory, new FakeToolset(), audit, NullLoggerFactory.Instance, TimeProvider.System);
        var synthesizer = new RunSynthesizer(factory, audit, TimeProvider.System, NullLogger<RunSynthesizer>.Instance);
        return new RalphLoop(iteration, completion, new MemoryStateStore(), synthesizer, audit, TimeProvider.System,
            NullLogger<RalphLoop>.Instance, observer);
    }

    [Fact]
    public async Task The_observer_sees_every_iteration_in_order()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("first guess")),
            ScriptedChatClient.Final(ScriptedChatClient.Json("second guess")),
            ScriptedChatClient.Final(ScriptedChatClient.SynthesisJson("done")));
        var recorder = new Recorder();

        var run = await Loop(model, new ScriptedCompletion(false, true), recorder)
            .RunAsync(TestTasks.Create(maxIterations: 5), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.Equal([1, 2], recorder.Numbers.Take(2));
    }

    [Fact]
    public async Task A_failing_observer_does_not_change_the_outcome()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Final(ScriptedChatClient.Json("postgres is back")),
            ScriptedChatClient.Final(ScriptedChatClient.SynthesisJson("done")));

        var run = await Loop(model, new ScriptedCompletion(true), new Throwing())
            .RunAsync(TestTasks.Create(maxIterations: 3), null, CancellationToken.None);

        Assert.Equal(RunOutcome.Resolved, run.Outcome);
        Assert.Single(run.Iterations);
    }
}
