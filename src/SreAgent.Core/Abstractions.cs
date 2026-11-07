using Microsoft.Extensions.AI;

namespace SreAgent.Core;

/// <summary>R of RALF: deterministic context gathering, done by code before the model is asked anything.</summary>
public interface IObservationCollector
{
    Task<string> CollectAsync(TaskSpec task, CancellationToken ct);
}

/// <summary>Decides whether the Ralph loop may stop. Code, never the model: models tend to declare success early.</summary>
public interface ICompletionCheck
{
    Task<CompletionStatus> CheckAsync(TaskSpec task, int iteration, CancellationToken ct);
}

/// <summary>The tools the model may call. In Phase 7 all of them are read-only.</summary>
public interface IAgentToolset
{
    IReadOnlyList<AIFunction> Tools { get; }
}

/// <summary>Creates the chat client for a run; <paramref name="model"/> overrides the configured default.</summary>
public interface IChatClientFactory
{
    (IChatClient Client, string Model) Create(string? model);
}

public sealed record AuditEvent(DateTimeOffset Ts, string RunId, int Iteration, string Event, object? Data);

/// <summary>Append-only record of everything the agent saw, decided and did.</summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken ct);
}

public interface IRunStateStore
{
    Task<RunState?> LoadAsync(string runId, CancellationToken ct);
    Task SaveAsync(RunState state, CancellationToken ct);
}
