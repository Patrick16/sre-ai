# 04. Observability: метрики, логи, алерты

## Зачем

SRE начинается с вопроса «откуда мы знаем, что система работает?». Ответ — телеметрия.
Три вида сигналов («три столпа»):

| Сигнал | Что это | Вопрос, на который отвечает | Инструмент в проекте |
|---|---|---|---|
| **Метрики** | Числа во времени (RPS, latency, % ошибок, память) | *Что* происходит и *насколько* плохо | Prometheus |
| **Логи** | Текстовые события с контекстом | *Почему* это произошло | Loki |
| **Трейсы** | Путь одного запроса через все сервисы | *Где* именно тормозит | (опционально) Tempo + OpenTelemetry |

Агент использует все доступные сигналы так же, как дежурный инженер: алерт → метрики → логи.

## Компоненты

### Prometheus
- **Pull-модель**: Prometheus сам ходит по HTTP на `/metrics` каждого сервиса раз в N секунд (scrape).
- **Что ему нужно знать**: кого скрейпить. В k8s это описывается CRD `ServiceMonitor`/`PodMonitor`
  (от prometheus-operator), а не вручную.
- **PromQL** — язык запросов. Примеры, которые понадобятся:
  - `sum(rate(http_requests_total{namespace="sandbox"}[5m])) by (service)` — RPS по сервисам
  - `histogram_quantile(0.99, sum(rate(http_request_duration_seconds_bucket[5m])) by (le, service))` — p99 latency
  - `kube_pod_container_status_restarts_total` — рестарты (из kube-state-metrics)
- **API**: `GET /api/v1/query?query=...` — именно так агент будет получать метрики.

### Вспомогательные экспортеры (идут в kube-prometheus-stack)
- **node-exporter** — метрики нод (CPU, память, диск).
- **kube-state-metrics** — состояние объектов k8s как метрики (реплики, рестарты, статусы подов).
- **cAdvisor** (в kubelet) — потребление ресурсов контейнерами.

### Alertmanager
- Prometheus вычисляет **правила алертов** (`PrometheusRule`): «если p99 > 300ms 5 минут — fire».
- Alertmanager получает сработавшие алерты и решает, что с ними делать: группировка,
  дедупликация, подавление (inhibit), тишина (silence), маршрутизация получателям.
- **В проекте**: receiver типа `webhook` → HTTP-эндпоинт агента. Так алерт становится триггером Ralph loop, а его resolve — частью критерия готовности.

### Loki
- Хранилище логов, индексирующее только **labels** (namespace, pod, app), а не весь текст — поэтому дешёвое.
- Логи собирает агент на каждой ноде (Grafana Alloy или Promtail) и отправляет в Loki.
- **LogQL**: `{namespace="sandbox", app="orders-api"} |= "error" | json | level="Error"`.
- **Важно для сервисов**: писать **структурные логи** (JSON) — их проще фильтровать и людям, и агенту.

### Grafana
- UI поверх Prometheus и Loki: дашборды, Explore, корреляция «метрика → логи того же пода за то же время».
- Дашборды храним как код (JSON в ConfigMap с label для sidecar-загрузчика) — так же, как в проде.

## Что инструментируем в .NET-сервисах

- Метрики: OpenTelemetry .NET SDK + Prometheus exporter (или `prometheus-net`). ASP.NET Core
  даёт базовые HTTP-метрики почти бесплатно.
- Логи: `Microsoft.Extensions.Logging` с JSON-консолью — k8s собирает stdout.
- Health-эндпоинты для probes: `/health/live`, `/health/ready`.
- (Опционально) трейсы через OpenTelemetry → Tempo.

## SLI, SLO, error budget

- **SLI** (индикатор): измеримая величина, отражающая опыт пользователя. Например:
  «доля запросов к orders-api, отвеченных успешно быстрее 300 мс».
- **SLO** (цель): «SLI ≥ 99% за 30 дней».
- **Error budget**: 1% — сколько можно «ошибаться». Если бюджет тратится быстро — заморозить релизы, чинить надёжность.
- **Алерты по burn rate**: алертить не на каждый всплеск, а когда бюджет сжигается слишком быстро
  (multi-window, multi-burn-rate). Это зрелый подход из SRE Book — реализуем в фазе 5.
- **Golden signals** (Google SRE): latency, traffic, errors, saturation — базовый дашборд для каждого сервиса.

## Как это видит агент

| Источник | API | Что агент берёт |
|---|---|---|
| Alertmanager | webhook (push) + `GET /api/v2/alerts` | Что сейчас горит, labels алерта |
| Prometheus | `GET /api/v1/query`, `/query_range` | Значения метрик вокруг времени инцидента |
| Loki | `GET /loki/api/v1/query_range` | Логи подов из labels алерта |
| k8s API | KubernetesClient для .NET | Статусы подов, события, история rollout, описание ресурсов |

Принцип: агент получает **сжатые** данные (агрегаты, топ ошибок, последние N строк),
а не сырые гигабайты — у LLM ограничено окно контекста (особенно у локальных моделей).
