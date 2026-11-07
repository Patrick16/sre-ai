# 02. Архитектура

## Общая картина

```
                         ┌──────────────────────────── kind cluster ─────────────────────────────┐
  Браузер / curl         │                                                                       │
  https://sandbox.localhost ► traefik ──► frontend ──► orders-api ◄── worker                     │
                         │      (TLS от          │  mTLS       │   mTLS       │                   │
                         │    cert-manager)      └─────── Linkerd sidecars ───┘                   │
                         │                                     │                                 │
                         │                                     ▼                                 │
                         │                               PostgreSQL (StatefulSet + PVC)          │
                         │                                                                       │
                         │  ── observability ─────────────────────────────────────────────────   │
                         │  Prometheus ◄── scrape метрик всех подов, node-exporter, kube-state   │
                         │  Loki       ◄── логи всех подов (через сборщик логов)                 │
                         │  Alertmanager ◄── правила алертов из Prometheus                       │
                         │  Grafana    ──► дашборды поверх Prometheus и Loki                     │
                         │                                                                       │
                         │  ── chaos ─────────────────────────────────────────────────────────   │
                         │  Chaos Mesh / .NET chaos tool ──► ломает поды/сеть в namespace sandbox│
                         │                                                                       │
                         │  ── agent ─────────────────────────────────────────────────────────   │
                         │  sre-agent (.NET) ◄── webhook от Alertmanager / таймер                │
                         │     │ читает: k8s API, Prometheus API, Loki API, Alertmanager API     │
                         │     │ действует: только через Guardrails → k8s API (узкий RBAC)       │
                         │     └──► отчёты (PVC / reports/)                                      │
                         └─────┼─────────────────────────────────────────────────────────────────┘
                               ▼
                 LLM endpoint (OpenAI-совместимый): облачный API  или  LM Studio на хосте
```

## Namespaces

| Namespace | Что там | Зачем отдельно |
|---|---|---|
| `sandbox` | frontend, orders-api, worker, postgres | Подопытная система; цель для хаоса и агента |
| `traefik` | Ingress Controller | Инфраструктура входящего трафика |
| `cert-manager` | Выпуск сертификатов | Инфраструктура |
| `linkerd`, `linkerd-cni`, `linkerd-viz` | Control plane mesh, CNI-плагин, дашборд | Инфраструктура |
| `monitoring` | Prometheus, Grafana, Alertmanager, Loki | Наблюдаемость не должна падать вместе с приложением |
| `chaos` | Chaos Mesh | Инструмент, отдельно от цели |
| `sre-agent` | Агент | Свой ServiceAccount и RBAC, изоляция прав |

Разделение по namespace — это одновременно организация, граница RBAC и граница
NetworkPolicy. Агент имеет право *действовать* только в `sandbox`.

## Sandbox-сервисы

Намеренно примитивные .NET Minimal API:

- **frontend** — принимает запрос, зовёт `orders-api`, отдаёт результат.
- **orders-api** — CRUD заказов в PostgreSQL, отдаёт метрики и логи.
- **worker** — фоновая обработка (опрашивает БД или очередь).

У каждого сервиса есть **«режимы неисправности»**, включаемые через ConfigMap/HTTP-эндпоинт:
задержка ответов, процент ошибок 500, утечка памяти, падение при старте. Это даёт
контролируемые проблемы для агента помимо Chaos Mesh.

## Агент: компоненты .NET-решения

| Проект | Ответственность |
|---|---|
| `SreAgent.Core` | Доменная модель (Observation, Finding, ActionProposal, Report), Ralph loop и RALF-итерация, RunState, интерфейсы tools и LLM, guardrails |
| `SreAgent.Tools` | Реализации tools: k8s (KubernetesClient), Prometheus, Loki, Alertmanager, действия |
| `SreAgent.LlmClients` | OpenAI-совместимый клиент (облако и LM Studio), tool calling, ретраи |
| `SreAgent.Host` | Точка входа: ASP.NET Core (webhook от Alertmanager) + BackgroundService (таймер), DI, конфигурация |
| `Sandbox.Services/*` | Подопытные сервисы |

Зависимости направлены внутрь: `Host → Tools, LlmClients → Core`. `Core` ничего не
знает о Kubernetes и конкретном LLM — это позволяет тестировать loop на фейках.

## Потоки данных

1. **Триггер**: Alertmanager шлёт webhook или срабатывает таймер (периодический health check).
2. **Retrieve**: агент собирает контекст: алерт, состояние подов, события, ключевые метрики, свежие логи.
3. **Analyze**: контекст + доступные tools уходят в LLM; LLM может запросить дополнительные данные
   (tool calls) — цикл повторяется ограниченное число шагов.
4. **Lead/Act**: если LLM предлагает действие — оно проходит guardrails; в зависимости от режима
   выполняется, ждёт подтверждения или только записывается.
5. **Feedback**: проверка результата (метрики восстановились?), запись отчёта и audit log.

Подробности — [05-agent-architecture.md](05-agent-architecture.md).

## Где запускается агент

- **Сначала — локально** (`dotnet run` на хосте, доступ к кластеру через kubeconfig,
  к Prometheus/Loki — через port-forward). Быстрый цикл разработки и отладки.
- **Потом — внутри кластера** как Deployment в `sre-agent` со своим ServiceAccount.
  LM Studio на хосте доступен из kind по `host.docker.internal`.
