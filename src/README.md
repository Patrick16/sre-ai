# src/

The .NET solution of the project. The solution file is `SreAi.slnx` in the repository root.

| Project | Type | Purpose | Phase |
|---|---|---|---|
| `SreAgent.Core` ✅ | classlib | `RalphLoop`, `RalfIteration`, `RunState`, `GuardedTool`/`ToolBudget`, prompts, response parsing, interfaces. Depends only on the Microsoft.Extensions.AI / Agent Framework abstractions — no Kubernetes, no concrete model | 7 |
| `SreAgent.Tools` ✅ | classlib | 6 read-only tools (KubernetesClient, Prometheus, Loki, Alertmanager), `ObservationCollector` (R), `AlertsResolvedCompletionCheck` (F) | 7 |
| `SreAgent.LlmClients` ✅ | classlib | OpenAI-compatible `IChatClient` (LM Studio / cloud) with GenAI telemetry | 7 |
| `SreAgent.Host` ✅ | ASP.NET Core | Alertmanager webhook, run queue and worker, schedule, reports, audit, metrics, HTTP API (`/runs`) | 5, 7 |
| `Sandbox.Services/*` ✅ | ASP.NET Core Minimal API | The guinea pigs: `frontend`, `orders-api` (from Phase 4 on PostgreSQL via Npgsql), `worker` (shared `Dockerfile`); fault modes are added in Phase 9 | 1, 4 |

Tests — `tests/SreAgent.Core.Tests` (xUnit): the behaviour of the harness with a "scripted" model
(`ScriptedChatClient`): stopping by the completion check, limits, fresh context, the step budget, truncation, tool errors.

Dependencies: `Host → Tools, LlmClients → Core`. Details: [docs/05](../docs/05-agent-architecture.md) (design)
and [docs/16](../docs/16-phase-7-agent-mvp.md) (implementation).
