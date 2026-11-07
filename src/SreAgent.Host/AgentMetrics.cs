using System.Diagnostics.Metrics;
using SreAgent.Core;

namespace SreAgent.Host;

/// <summary>The agent is a service too: how often it runs, how it ends, what it costs.</summary>
public sealed class AgentMetrics
{
    public const string MeterName = "SreAgent.Agent";

    private readonly Counter<long> _alerts;
    private readonly Counter<long> _runsStarted;
    private readonly Counter<long> _runsFinished;
    private readonly Counter<long> _runsDeduplicated;
    private readonly Counter<long> _iterations;
    private readonly Counter<long> _toolCalls;
    private readonly Counter<long> _tokens;
    private readonly Histogram<double> _runDuration;
    private readonly UpDownCounter<long> _active;

    public AgentMetrics(IMeterFactory factory)
    {
        var m = factory.Create(MeterName);
        _alerts = m.CreateCounter<long>("sre_agent.alerts.received", description: "Alerts received from Alertmanager");
        _runsStarted = m.CreateCounter<long>("sre_agent.runs.started", description: "Agent runs started, by trigger");
        _runsFinished = m.CreateCounter<long>("sre_agent.runs.finished", description: "Agent runs finished, by outcome");
        _runsDeduplicated = m.CreateCounter<long>("sre_agent.runs.deduplicated", description: "Alerts not starting a run because one is already active for the namespace");
        _iterations = m.CreateCounter<long>("sre_agent.iterations", description: "RALF iterations");
        _toolCalls = m.CreateCounter<long>("sre_agent.tool.calls", description: "Tool calls by tool and result");
        _tokens = m.CreateCounter<long>("sre_agent.llm.tokens", description: "Tokens used, by direction");
        _runDuration = m.CreateHistogram<double>("sre_agent.run.duration", unit: "s", description: "Wall time of a run");
        _active = m.CreateUpDownCounter<long>("sre_agent.runs.active", description: "Runs in progress");
    }

    public void AlertReceived(string status, string? name) =>
        _alerts.Add(1, new("status", status), new("alertname", name));

    public void Deduplicated() => _runsDeduplicated.Add(1);

    public void RunStarted(TriggerKind trigger)
    {
        _runsStarted.Add(1, new KeyValuePair<string, object?>("trigger", trigger.ToString()));
        _active.Add(1);
    }

    public void RunFinished(AgentRun run)
    {
        _active.Add(-1);
        _runsFinished.Add(1, new("trigger", run.Task.Trigger.Kind.ToString()), new("outcome", run.Outcome.ToString()));
        _runDuration.Record(((run.FinishedAt ?? run.StartedAt) - run.StartedAt).TotalSeconds);
        foreach (var it in run.Iterations)
        {
            _iterations.Add(1, new KeyValuePair<string, object?>("parsed", it.Analysis.Parsed));
            _tokens.Add(it.InputTokens, new KeyValuePair<string, object?>("direction", "input"));
            _tokens.Add(it.OutputTokens, new KeyValuePair<string, object?>("direction", "output"));
            foreach (var call in it.ToolCalls)
                _toolCalls.Add(1, new("tool", call.Tool), new("failed", call.Failed));
        }
    }
}
