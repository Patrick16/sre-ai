using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using SreAgent.Core;

namespace SreAgent.Core.Tests;

/// <summary>A "model" that answers from a script, and remembers what it was sent.</summary>
internal sealed class ScriptedChatClient(params Func<IList<ChatMessage>, ChatResponse>[] script) : IChatClient
{
    private readonly Queue<Func<IList<ChatMessage>, ChatResponse>> _script = new(script);
    public List<List<ChatMessage>> Requests { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        Options.Add(options);
        if (_script.Count == 0) throw new InvalidOperationException("script exhausted");
        return Task.FromResult(_script.Dequeue()(list));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var u in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return u;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public static Func<IList<ChatMessage>, ChatResponse> Final(string text) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 } };

    public static Func<IList<ChatMessage>, ChatResponse> CallTool(string tool, Dictionary<string, object?>? args = null) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, args ?? [])]));

    public static string Json(string hypothesis, string confidence = "medium", string? fact = null) =>
        $$"""{"summary":"s","hypothesis":"{{hypothesis}}","confidence":"{{confidence}}","evidence":["e1"],"facts":[{{(fact is null ? "" : $"\"{fact}\"")}}],"recommendations":["r1"],"next_checks":["n1"]}""";

    public static string SynthesisJson(string rootCause) =>
        $$"""{"summary":"sum","root_cause":"{{rootCause}}","confidence":"high","timeline":["21:47Z - db scaled to 0"],"evidence":["desired=0"],"recommendations":["r"],"disagreements":"iteration 8 contradicted 3-4"}""";
}

internal sealed class FixedFactory(IChatClient client) : IChatClientFactory
{
    public (IChatClient Client, string Model) Create(string? model) => (client, model ?? "fake-model");
}

internal sealed class FakeToolset(params AIFunction[] tools) : IAgentToolset
{
    public IReadOnlyList<AIFunction> Tools { get; } = tools;
}

internal sealed class FakeCollector(string text = "pods: all ready") : IObservationCollector
{
    public int Calls { get; private set; }
    public Task<string> CollectAsync(TaskSpec task, CancellationToken ct) { Calls++; return Task.FromResult(text); }
}

internal sealed class ScriptedCompletion(params bool[] doneByIteration) : ICompletionCheck
{
    public Task<CompletionStatus> CheckAsync(TaskSpec task, int iteration, CancellationToken ct)
    {
        var done = iteration <= doneByIteration.Length && doneByIteration[iteration - 1];
        return Task.FromResult(new CompletionStatus(done, done ? "alert resolved" : "alert still firing"));
    }
}

internal sealed class MemoryAudit : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];
    public Task WriteAsync(AuditEvent auditEvent, CancellationToken ct) { lock (Events) Events.Add(auditEvent); return Task.CompletedTask; }
}

internal sealed class MemoryStateStore : IRunStateStore
{
    public Dictionary<string, RunState> States { get; } = [];
    public int Saves { get; private set; }
    public Task<RunState?> LoadAsync(string runId, CancellationToken ct) => Task.FromResult(States.GetValueOrDefault(runId));
    public Task SaveAsync(RunState state, CancellationToken ct) { States[state.RunId] = state; Saves++; return Task.CompletedTask; }
}

internal static class TestTasks
{
    public static TaskSpec Create(int maxIterations = 3, int maxToolCalls = 4, int maxOutputChars = 4000) => new(
        "run-1",
        "Find out why OrdersApiErrorBudgetBurnFast fires and whether it resolves.",
        new TriggerEvent(TriggerKind.Alert, [], null, DateTimeOffset.UtcNow),
        "sandbox",
        new AgentLimits { MaxIterations = maxIterations, MaxToolCallsPerIteration = maxToolCalls, MaxToolOutputChars = maxOutputChars, Backoff = TimeSpan.Zero },
        "English");
}
