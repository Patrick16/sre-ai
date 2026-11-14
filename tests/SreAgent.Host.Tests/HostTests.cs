using Microsoft.Extensions.Options;
using SreAgent.Core;
using SreAgent.Host;

namespace SreAgent.Host.Tests;

internal static class Make
{
    public static RunQueue Queue(int suppressionMinutes = 240) =>
        new(Options.Create(new AgentOptions { AlertSuppressionMinutes = suppressionMinutes }), TimeProvider.System);

    public static RunRequest Request(RunQueue q, string? ns = null) =>
        new(q.NewTask(new TriggerEvent(TriggerKind.Manual, [], "test", DateTimeOffset.UtcNow), "goal", ns), null);
}

public class RunQueueTests
{
    [Fact]
    public void The_run_is_registered_before_a_worker_can_take_it_off_the_queue()
    {
        var q = Make.Queue();
        var request = Make.Request(q);

        Assert.True(q.TryEnqueue(request));

        // What the worker does the moment it reads the request: this used to throw (race in TryEnqueue).
        Assert.True(q.Reader.TryRead(out var taken));
        q.MarkRunning(taken!.Task.Id);
        Assert.Equal("running", q.Get(request.Task.Id)!.Status);
    }

    [Fact]
    public void MarkRunning_and_Complete_never_throw_for_an_unknown_run()
    {
        var q = Make.Queue();
        q.MarkRunning("nope");
        var task = Make.Request(q).Task;
        q.Complete(new AgentRun(task, DateTimeOffset.UtcNow) { Outcome = RunOutcome.Failed, OutcomeReason = "x" });
        Assert.Equal("finished", q.Get(task.Id)!.Status);
    }

    [Fact]
    public void One_run_per_namespace_until_it_completes()
    {
        var q = Make.Queue();
        var first = Make.Request(q);
        Assert.True(q.TryEnqueue(first));
        Assert.False(q.TryEnqueue(Make.Request(q)));
        Assert.Equal(first.Task.Id, q.ActiveRunFor("sandbox"));

        q.Complete(new AgentRun(first.Task, DateTimeOffset.UtcNow) { Outcome = RunOutcome.Resolved });
        Assert.True(q.TryEnqueue(Make.Request(q)));
    }

    [Fact]
    public void Abandon_frees_the_namespace()
    {
        var q = Make.Queue();
        var r = Make.Request(q);
        q.TryEnqueue(r);
        q.Abandon(r.Task);
        Assert.Null(q.ActiveRunFor("sandbox"));
    }

    [Fact]
    public void A_full_queue_leaves_no_trace()
    {
        var q = Make.Queue();
        for (var i = 0; i < 10; i++) Assert.True(q.TryEnqueue(Make.Request(q, $"ns{i}")));
        var overflow = Make.Request(q, "ns-overflow");

        Assert.False(q.TryEnqueue(overflow));
        Assert.Null(q.Get(overflow.Task.Id));
        Assert.Null(q.ActiveRunFor("ns-overflow"));
    }

    [Fact]
    public void Finished_runs_are_not_remembered_forever()
    {
        var q = Make.Queue();
        var ids = new List<string>();
        for (var i = 0; i < 250; i++)
        {
            var r = Make.Request(q);
            Assert.True(q.TryEnqueue(r));
            q.Reader.TryRead(out _);
            q.Complete(new AgentRun(r.Task, DateTimeOffset.UtcNow) { Outcome = RunOutcome.Resolved });
            ids.Add(r.Task.Id);
        }
        Assert.NotNull(q.Get(ids[^1]));       // the newest is still there
        Assert.Null(q.Get(ids[0]));           // the oldest finished one was trimmed
    }

    [Fact]
    public void A_still_firing_alert_does_not_start_a_new_run_until_it_resolves_or_the_window_passes()
    {
        var q = Make.Queue(suppressionMinutes: 240);
        Assert.False(q.AlreadyHandled(["fp1"]));
        q.RememberHandled(["fp1", "fp2"]);

        Assert.True(q.AlreadyHandled(["fp1"]));
        Assert.True(q.AlreadyHandled(["fp1", "fp2"]));
        Assert.False(q.AlreadyHandled(["fp1", "fp3"]));         // a NEW alert in the group: investigate

        q.ForgetHandled("fp1");                                  // resolved, then fires again = new problem
        Assert.False(q.AlreadyHandled(["fp1"]));
    }

    [Fact]
    public void Suppression_can_be_switched_off()
    {
        var q = Make.Queue(suppressionMinutes: 0);
        q.RememberHandled(["fp1"]);
        Assert.False(q.AlreadyHandled(["fp1"]));
    }
}

public class ApiTokenTests
{
    [Theory]
    [InlineData("", null, true)]            // not configured: not required
    [InlineData("", "anything", true)]
    [InlineData("s3cret", null, false)]
    [InlineData("s3cret", "", false)]
    [InlineData("s3cret", "wrong", false)]
    [InlineData("s3cret", "s3cret", true)]
    public void Token_is_checked_only_when_configured(string configured, string? supplied, bool expected) =>
        Assert.Equal(expected, ApiToken.IsValid(configured, supplied));
}

public class ApprovalGateTests
{
    [Fact]
    public async Task A_human_decision_completes_the_request()
    {
        var gate = new ApprovalGate(TimeProvider.System);
        var request = new ApprovalRequest("r1", "run", 1, "sandbox", new ActionProposal("restart_deployment", "orders-api", "why"), DateTimeOffset.UtcNow.AddMinutes(5));

        var waiting = gate.RequestAsync(request, CancellationToken.None);
        Assert.Single(gate.Pending());
        Assert.True(gate.Decide("r1", true, "alice"));
        var decision = await waiting;

        Assert.True(decision.Approved);
        Assert.Equal("alice", decision.By);
        Assert.Empty(gate.Pending());
        Assert.False(gate.Decide("r1", true, "alice"));          // already decided
    }

    [Fact]
    public async Task No_decision_in_time_means_no()
    {
        var gate = new ApprovalGate(TimeProvider.System);
        var request = new ApprovalRequest("r2", "run", 1, "sandbox", new ActionProposal("delete_pod", "p", "why"), DateTimeOffset.UtcNow.AddMilliseconds(50));

        var decision = await gate.RequestAsync(request, CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.True(decision.Expired);
    }
}

public class FileActionLedgerTests
{
    [Fact]
    public async Task Entries_survive_a_restart_and_old_ones_are_dropped()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            var now = DateTimeOffset.UtcNow;
            var ledger = new FileActionLedger(path, TimeProvider.System);
            await ledger.RecordAsync(now.AddDays(-2), "restart_deployment/old", CancellationToken.None);
            await ledger.RecordAsync(now, "restart_deployment/orders-api", CancellationToken.None);

            var reloaded = new FileActionLedger(path, TimeProvider.System);
            var entries = reloaded.Since(now.AddHours(-1));

            Assert.Single(entries);
            Assert.Equal("restart_deployment/orders-api", entries[0].Key);
            Assert.Single(reloaded.Since(DateTimeOffset.MinValue));   // the 2-day-old entry was pruned on write
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DataRetentionTests
{
    [Fact]
    public void Only_old_json_files_are_removed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var oldFile = Path.Combine(dir, "old.json");
            var newFile = Path.Combine(dir, "new.json");
            var other = Path.Combine(dir, "keep.txt");
            File.WriteAllText(oldFile, "{}");
            File.WriteAllText(newFile, "{}");
            File.WriteAllText(other, "x");
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-30));

            var removed = DataRetention.PruneOldFiles(dir, TimeSpan.FromDays(14), DateTime.UtcNow);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(newFile));
            Assert.True(File.Exists(other));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Zero_retention_keeps_everything_and_a_missing_directory_is_fine()
    {
        Assert.Equal(0, DataRetention.PruneOldFiles("Z:/does/not/exist", TimeSpan.FromDays(1), DateTime.UtcNow));
        Assert.Equal(0, DataRetention.PruneOldFiles(Path.GetTempPath(), TimeSpan.Zero, DateTime.UtcNow));
    }
}

public class ReportWriterTests
{
    [Theory]
    [InlineData("PostgresDown", "postgresdown")]
    [InlineData("Postgres Down!", "postgres-down")]
    [InlineData("éèà!!", "report")]                              // non-Latin-only titles used to give an empty slug
    [InlineData("", "report")]
    public void Slug_is_never_empty(string title, string expected) =>
        Assert.Equal(expected, MarkdownReportWriter.Slug(title));

    [Fact]
    public void Slug_is_cut_to_50_characters_without_a_trailing_dash() =>
        Assert.Equal(new string('a', 49), MarkdownReportWriter.Slug(new string('a', 49) + "-bbbbbbbb"));

    [Theory]
    [InlineData("", 0)]
    [InlineData("no fences", 0)]
    [InlineData("a ``` b", 3)]
    [InlineData("`` and ````` and `", 5)]
    public void Longest_backtick_run(string text, int expected) =>
        Assert.Equal(expected, MarkdownReportWriter.LongestBacktickRun(text));

    [Fact]
    public async Task Observations_with_a_code_fence_do_not_break_the_report_block()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var task = new TaskSpec("id1", "goal", new TriggerEvent(TriggerKind.Manual, [], "t", DateTimeOffset.UtcNow), "sandbox", new AgentLimits(), "English");
            var run = new AgentRun(task, DateTimeOffset.UtcNow) { Outcome = RunOutcome.Resolved, OutcomeReason = "ok", FinishedAt = DateTimeOffset.UtcNow };
            var analysis = new Analysis("s", "h", "low", [], [], [], true, "");
            run.Iterations.Add(new IterationResult(1, DateTimeOffset.UtcNow, TimeSpan.Zero, "m", "log line with ``` inside", analysis, [], "lead", new CompletionStatus(true, "ok"), 0, 0));

            var path = await new MarkdownReportWriter(dir).WriteAsync(run, CancellationToken.None);
            var text = await File.ReadAllTextAsync(path);

            Assert.Contains("````text", text);                   // fence is one longer than the longest run inside
            Assert.Contains("## Summary", text);                 // headings are always English
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class CorruptStateTests
{
    [Fact]
    public async Task A_damaged_ledger_file_is_set_aside_and_the_agent_starts_with_an_empty_ledger()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "ledger.json");
            File.WriteAllText(path, "[{\"at\": \"2026-11-10T10:00:00Z\", \"ke");     // a write that was cut off

            var ledger = new FileActionLedger(path, TimeProvider.System);          // used to throw here, at host start

            Assert.Empty(ledger.Since(DateTimeOffset.MinValue));
            Assert.True(File.Exists(path + ".corrupt"));
            await ledger.RecordAsync(DateTimeOffset.UtcNow, "restart_deployment/x", CancellationToken.None);   // and it works again
            Assert.Single(new FileActionLedger(path, TimeProvider.System).Since(DateTimeOffset.MinValue));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RunState_with_an_unmeasured_error_ratio_can_be_saved_and_loaded()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new FileRunStateStore(dir);
            var state = new RunState { RunId = "r1" };
            var proposal = new ActionProposal("restart_deployment", "orders-api", "why");
            var snapshot = new HealthSnapshot(DateTimeOffset.UtcNow, double.NaN, 1);
            state.Actions.Add(new ActionRecord(1, DateTimeOffset.UtcNow, proposal, ActionMode.Auto, ActionStatus.Executed, "d", [], snapshot, snapshot));

            await store.SaveAsync(state, CancellationToken.None);           // NaN used to be impossible to serialize
            var loaded = await store.LoadAsync("r1", CancellationToken.None);

            Assert.True(double.IsNaN(loaded!.Actions[0].Before!.ErrorRatio));
        }
        finally { Directory.Delete(dir, true); }
    }
}
