using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SreAgent.Core;
using SreAgent.Host;
using SreAgent.LlmClients;

namespace SreAgent.Host.Tests;

/// <summary>Choosing a provider, a model and a key: the rules a request cannot bend.</summary>
public class LlmProviderTests
{
    private static OpenAiCompatibleChatClientFactory Factory(Action<LlmOptions>? configure = null)
    {
        var o = new LlmOptions { Model = "local-a", AllowedModels = ["local-b"] };
        o.Providers["cloud"] = new LlmOptions.ProviderOptions { BaseUrl = "https://llm.example.test/v1" };
        o.Providers["listed"] = new LlmOptions.ProviderOptions { BaseUrl = "https://listed.example.test/v1", Models = ["m1", "m2"], RequiresKey = false };
        configure?.Invoke(o);
        return new OpenAiCompatibleChatClientFactory(Options.Create(o), NullLoggerFactory.Instance);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "qwen/qwen3.5-9b", "qwen/qwen3.5-9b")]
    [InlineData("cloud", "vendor/model:free", "cloud::vendor/model:free")]
    [InlineData("cloud", null, "cloud::")]
    public void A_choice_survives_the_round_trip_through_one_string(string? provider, string? model, string? spec)
    {
        Assert.Equal(spec, LlmRef.Format(provider, model));
        Assert.Equal((provider, model), LlmRef.Parse(spec));
    }

    [Fact]
    public void Nothing_chosen_means_the_configured_default()
    {
        var f = Factory();
        Assert.Equal(("local", "local-a", null), f.Resolve(null));
        Assert.Equal(("local", "local-b", null), f.Resolve("local-b"));          // the old API: a model of the built-in provider
    }

    [Fact]
    public void Unknown_providers_and_models_outside_the_allowlist_are_refused()
    {
        var f = Factory();
        Assert.Contains("not configured", f.Resolve("elsewhere::x").Error);
        Assert.Contains("not allowed", f.Resolve("local::gpt").Error);
        Assert.Contains("not allowed", f.Resolve("listed::m3").Error);
        Assert.Null(f.Resolve("listed::m2").Error);
        Assert.Equal("m1", f.Resolve("listed::").Model);                           // a provider's first model is its default
    }

    [Fact]
    public void A_provider_that_needs_a_key_is_refused_until_one_is_set_and_the_key_is_never_listed()
    {
        var f = Factory();
        Assert.Contains("needs an API key", f.Resolve("cloud::any-model").Error);

        Assert.True(f.SetKey("cloud", "sk-secret-123"));

        Assert.Null(f.Resolve("cloud::any-model").Error);
        var info = f.Providers.Single(p => p.Name == "cloud");
        Assert.True(info.KeySet);
        Assert.Equal("runtime", info.KeySource);
        Assert.DoesNotContain("sk-secret-123", System.Text.Json.JsonSerializer.Serialize(f.Providers));

        f.SetKey("cloud", "");
        Assert.Contains("needs an API key", f.Resolve("cloud::any-model").Error);
        Assert.False(f.SetKey("elsewhere", "x"));
    }

    [Fact]
    public void The_default_can_be_moved_only_to_a_valid_choice()
    {
        var f = Factory();
        Assert.NotNull(f.SetDefault("cloud", "m"));                                  // no key yet
        Assert.Equal(("local", "local-a"), f.Default);

        f.SetKey("cloud", "k");
        Assert.Null(f.SetDefault("cloud", "vendor/m"));

        Assert.Equal(("cloud", "vendor/m"), f.Default);
        Assert.Equal(("cloud", "vendor/m", null), f.Resolve(null));
    }

    [Fact]
    public void A_queued_run_keeps_the_model_it_was_queued_with()
    {
        var f = Factory();
        var q = Make.Queue();
        var request = RunRequest.For(Make.Request(q).Task, f);

        f.SetDefault("local", "local-b");

        Assert.Equal("local::local-a", request.Model);
        Assert.Equal(("local", "local-a"), (request.Provider, request.ModelName));
    }
}

/// <summary>The run list and the traces survive an agent restart.</summary>
public class RunHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sre-agent-history-" + Guid.NewGuid().ToString("N"));
    private RunHistory History() => new(_dir, NullLogger<RunHistory>.Instance);
    private RunQueue Queue() => new(Options.Create(new AgentOptions()), TimeProvider.System, History());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_finished_run_is_still_listed_after_a_restart_with_who_started_it()
    {
        var q = Queue();
        var task = q.NewTask(new TriggerEvent(TriggerKind.Schedule, [], "routine check every 60 min", DateTimeOffset.UtcNow), "goal", mode: ActionMode.Approve);
        Assert.True(q.TryEnqueue(new RunRequest(task, "local::m") { Provider = "local", ModelName = "m" }));
        q.Complete(new AgentRun(task, DateTimeOffset.UtcNow) { Outcome = RunOutcome.Resolved, OutcomeReason = "ok", FinishedAt = DateTimeOffset.UtcNow });

        var restored = Queue().Get(task.Id)!;

        Assert.Equal(("Schedule", "finished", "Resolved"), (restored.Trigger, restored.Status, restored.Outcome));
        Assert.Equal(("routine check every 60 min", "local", "m", "Approve"), (restored.TriggerDetail, restored.Provider, restored.Model, restored.Mode));
    }

    [Fact]
    public void A_run_cut_off_by_a_restart_is_shown_as_cancelled_not_as_running_forever()
    {
        var q = Queue();
        var request = Make.Request(q);
        q.TryEnqueue(request);
        q.MarkRunning(request.Task.Id);

        var afterRestart = Queue();

        var restored = afterRestart.Get(request.Task.Id)!;
        Assert.Equal(("finished", "Cancelled"), (restored.Status, restored.Outcome));
        Assert.True(afterRestart.TryEnqueue(Make.Request(afterRestart)));            // and it does not block the namespace
    }

    [Fact]
    public void Iterations_are_read_back_from_disk_when_memory_has_forgotten_them()
    {
        var task = new TaskSpec("20261111-101500-manual-abc123", "goal", new TriggerEvent(TriggerKind.Manual, [], "t", DateTimeOffset.UtcNow), "sandbox", new AgentLimits(), "English");
        var iteration = new IterationResult(1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), "m", "observed",
            new Analysis("summary", "hypothesis", "high", [], [], [], Parsed: true, RawText: "raw"), [], "lead", new CompletionStatus(true, "done"), 1, 2);
        new RunTraceStore(History()).IterationFinished(task, iteration);

        var views = new RunTraceStore(History()).Get(task.Id);                          // a new process: empty memory

        Assert.Equal("hypothesis", Assert.Single(views).Hypothesis);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_run_id_from_a_url_never_becomes_a_path(string id) => Assert.Empty(History().LoadTrace(id));
}
