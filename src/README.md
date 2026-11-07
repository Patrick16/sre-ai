# src/

.NET-решение проекта. Solution — `SreAi.slnx` в корне репозитория.

| Проект | Тип | Назначение | Фаза |
|---|---|---|---|
| `SreAgent.Core` ✅ | classlib | `RalphLoop`, `RalfIteration`, `RunState`, `GuardedTool`/`ToolBudget`, промпты, разбор ответа, интерфейсы. Зависит только от абстракций Microsoft.Extensions.AI / Agent Framework — ни Kubernetes, ни конкретной модели | 7 |
| `SreAgent.Tools` ✅ | classlib | 6 read-only tools (KubernetesClient, Prometheus, Loki, Alertmanager), `ObservationCollector` (R), `AlertsResolvedCompletionCheck` (F) | 7 |
| `SreAgent.LlmClients` ✅ | classlib | OpenAI-совместимый `IChatClient` (LM Studio / облако) с GenAI-телеметрией | 7 |
| `SreAgent.Host` ✅ | ASP.NET Core | Webhook Alertmanager, очередь и worker прогонов, расписание, отчёты, audit, метрики, HTTP API (`/runs`) | 5, 7 |
| `Sandbox.Services/*` ✅ | ASP.NET Core Minimal API | Подопытные `frontend`, `orders-api` (с Фазы 4 — PostgreSQL через Npgsql), `worker` (общий `Dockerfile`); режимы неисправности добавятся в Фазе 9 | 1, 4 |

Тесты — `tests/SreAgent.Core.Tests` (xUnit): поведение harness на «скриптованной» модели
(`ScriptedChatClient`): остановка по completion check, лимиты, свежий контекст, бюджет шагов, усечение, ошибки tools.

Зависимости: `Host → Tools, LlmClients → Core`. Подробно — [docs/05](../docs/05-agent-architecture.md) (дизайн)
и [docs/16](../docs/16-phase-7-agent-mvp.md) (реализация).
