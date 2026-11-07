# src/

.NET-решение проекта. Пока только папки-заглушки; `SreAi.sln` и `.csproj` создаются в фазе 0.

| Проект | Тип | Назначение | Фаза |
|---|---|---|---|
| `SreAgent.Core` | classlib | Доменная модель, Ralph loop + RALF-итерация, RunState, интерфейсы tools/LLM, guardrails. Без зависимостей от k8s и LLM | 7–8 |
| `SreAgent.Tools` | classlib | Реализации tools: KubernetesClient, Prometheus, Loki, Alertmanager, исполнители действий | 7–8 |
| `SreAgent.LlmClients` | classlib | OpenAI-совместимый клиент (облако / LM Studio), tool calling, ретраи | 7 |
| `SreAgent.Host` | ASP.NET Core | Webhook для Alertmanager, BackgroundService-таймер, DI, конфиг | 7 |
| `Sandbox.Services/*` | ASP.NET Core Minimal API | Подопытные `frontend`, `orders-api`, `worker` с режимами неисправности | 1 |

Тесты будут рядом: `tests/SreAgent.Core.Tests` и т.д. (xUnit). Важнее всего покрыть
тестами guardrails и сам loop (на фейковой LLM).

Зависимости: `Host → Tools, LlmClients → Core`. Подробно — [docs/02-architecture.md](../docs/02-architecture.md)
и [docs/05-agent-architecture.md](../docs/05-agent-architecture.md).
