# 14. Фаза 5: observability на практике — метрики, логи, SLO, алерты

Теория — [04-observability-stack.md](04-observability-stack.md). Здесь — что развёрнуто, как связано и что
показала проверка на живом кластере.

## Что развёрнуто

| Компонент | Версия / чарт | Namespace | Роль |
|---|---|---|---|
| kube-prometheus-stack | 93.0.0 | `monitoring` | Prometheus Operator, Prometheus (PVC 5Gi, retention 5d — дольше самого длинного окна SLO, 3d), Alertmanager, Grafana, node-exporter, kube-state-metrics, готовые правила и дашборды для Kubernetes |
| Loki | grafana/loki 7.5.0, режим SingleBinary | `monitoring` | Хранилище логов (файлы на PVC, retention 72h) |
| Grafana Alloy | grafana/alloy 1.15.0, DaemonSet | `monitoring` | Сбор логов `/var/log/pods` на каждой ноде → Loki |
| [sandbox-observability](../infra/helm/sandbox-observability/) | наш чарт | `monitoring` | Дашборды как код |
| [loadgen](../infra/helm/loadgen/) | наш чарт, k6 2.3.0 | `sandbox` | Постоянные ~5 rps через Traefik |
| sre-agent (заглушка) | наш `sandbox-service` | `sre-agent` | Принимает webhook Alertmanager, логирует, `GET /alerts` |
| postgres-exporter | v0.20.1, sidecar в чарте postgres | `sandbox` | Метрики БД (`pg_up`, соединения, размер) |

| UI | Адрес | Доступ |
|---|---|---|
| Grafana | https://grafana.localhost | `admin`, пароль: `./scripts/grafana-password.ps1` |
| Prometheus | https://prometheus.localhost | без аутентификации |
| Alertmanager | https://alertmanager.localhost | без аутентификации |

> Prometheus и Alertmanager без аутентификации — допустимо **только** потому, что kind теперь слушает порты 80/443
> исключительно на `127.0.0.1` (`listenAddress` в [kind-config.yaml](../infra/cluster/kind-config.yaml)). До Фазы 5
> они были открыты на `0.0.0.0` — то есть всей локальной сети. В проде перед такими UI ставят oauth2-proxy/SSO.

```powershell
./scripts/deploy-infra.ps1   # + kube-prometheus-stack, Loki, Alloy, дашборды (первым — ради CRD)
./scripts/build-images.ps1
./scripts/deploy-apps.ps1    # + loadgen, sre-agent
```

## Как всё связано

```
                          ┌──────────── pull каждые 15 с ─────────────┐
 .NET-сервисы :9090/metrics ◄── PodMonitor ──┐                        │
 postgres-exporter :9187    ◄── ServiceMonitor┤◄── Prometheus Operator ─┤──► Prometheus ──► PrometheusRule (SLO, PostgresDown,
 Traefik, cert-manager      ◄── ServiceMonitor┘    (генерирует           │       │                  + ~150 готовых правил k8s)
 kubelet/cAdvisor, node-exporter, kube-state-metrics   scrape-конфиг)    │       ▼
                                                                         │   Alertmanager ──webhook──► sre-agent /webhooks/alertmanager
 stdout всех контейнеров ─► /var/log/pods на ноде ─► Alloy (DaemonSet) ─► Loki        │
                                                                                       ▼
                                          Grafana ◄── Prometheus + Loki (дашборд Sandbox / Golden signals)
```

## Ключевые решения

| Решение | Почему |
|---|---|
| Метрики через **OpenTelemetry** .NET + Prometheus-экспортер (`1.20.1-beta.1`) | Стандартные имена (semantic conventions): `http_server_request_duration_seconds` с `http_route`, `http_response_status_code`. Экспортер всё ещё beta — осознанный выбор: официальный путь OTel к pull-модели Prometheus |
| Встроенные метры .NET, а не свои | ASP.NET Core, Kestrel, HttpClient, рантайм (GC/потоки) и Npgsql (пул соединений) уже публикуют метрики — достаточно `AddMeter(...)` |
| `/metrics` на **отдельном порту 9090** | Иначе Ingress `/api/*` отдал бы метрики orders-api наружу. Проверка — по реальному порту соединения (`Connection.LocalPort`), а не по заголовку `Host`, которым управляет клиент |
| JSON-логи через `Logging:Console:FormatterName=json` | Без изменения кода; Alloy вынимает `LogLevel` в метку `level` |
| **Alloy**, а не Promtail | Promtail достиг end-of-life в марте 2026 |
| DaemonSet Alloy с **toleration** на control-plane | Иначе логи Traefik (он на управляющей ноде) молча не собирались бы |
| `*SelectorNilUsesHelmValues: false` | По умолчанию Prometheus видит только ServiceMonitor'ы с меткой своего релиза, а остальные **молча игнорирует** |
| etcd/scheduler/controller-manager/kube-proxy не скрейпим | В kind они слушают 127.0.0.1 внутри ноды; в EKS/GKE их не скрейпить вовсе. Иначе — вечные «target down» |
| Namespace `monitoring` — Pod Security `privileged` | node-exporter и Alloy читают хост (`hostNetwork`, `/proc`, `/var/log`). Только инфраструктура, никаких приложений |
| Отключены `NodeClockNotSynchronising/SkewDetected` | Ноды kind — контейнеры на часах хоста (WSL2), статус NTP «мигает» ложно |
| SLO и алерты — **в чарте сервиса** (`slo:` в values) | Владелец сервиса владеет своим SLO; одинаковые правила для всех сервисов из шаблона |

## SLO и burn-rate алерты

**SLI** = доля «хороших» запросов: не 5xx **и** быстрее порога. **SLO** = 99% (бюджет ошибок — 1%).

| Сервис | Порог | Почему такой |
|---|---|---|
| orders-api | 250 мс | `0.25` — граница bucket'а гистограммы ASP.NET Core |
| frontend | 500 мс | включает ожидание orders-api |

**Порог SLO обязан совпадать с границей bucket'а** (`le="0.25"`). Изначально в роадмапе было «300 мс», но такого
bucket'а нет — посчитать «доля запросов быстрее 300 мс» из этой гистограммы невозможно.

Recording rules `slo:sli_error:ratio_rate{5m,30m,1h,2h,6h,1d,3d}` и четыре алерта (Google SRE Workbook, multi-window multi-burn-rate):

| Алерт | Длинное окно | Короткое окно | Burn rate | `for` | Severity | Смысл |
|---|---|---|---|---|---|---|
| `…ErrorBudgetBurnFast` | 1h | 5m | 14.4 | 2m | critical | 2% месячного бюджета за час |
| `…ErrorBudgetBurnFast` | 6h | 30m | 6 | 15m | critical | 5% за 6 часов |
| `…ErrorBudgetBurnSlow` | 1d | 2h | 3 | 1h | warning | 10% за сутки |
| `…ErrorBudgetBurnSlow` | 3d | 6h | 1 | 3h | warning | 10% за 3 дня |

Длинное окно — «это значимо», короткое — «это всё ещё происходит» (иначе алерт горел бы час после починки).

Две ловушки PromQL, обработанные в [шаблоне](../infra/helm/sandbox-service/templates/prometheusrule-slo.yaml):
- **`or vector(0)`**: при полном отказе «хороших» запросов нет вовсе, сумма по ним — пустой вектор, и всё выражение
  тоже пустое → **алерт молчит ровно тогда, когда он нужнее всего**.
- **`http_route=~".+"`**: скрейпы `/metrics` и неизвестные адреса (404) попадают в гистограмму без метки маршрута;
  без фильтра они «разбавляли» бы SLO хорошими запросами.

Похожая ловушка в `PostgresDown`: `pg_up == 0` ловит «экспортер жив, база нет», но если под базы исчез целиком,
серии нет вовсе — нужен `absent(pg_up{...})`.

## Что показала проверка

### 1. Сквозной сценарий: база упала → алерт → агент
Под постоянной нагрузкой: `helm upgrade postgres --set replicas=0`.

| Время | Событие |
|---|---|
| 0:00 | База остановлена; orders-api отвечает 503, frontend — 502 |
| 0:32 | `PostgresDown` — pending |
| 1:18 | `OrdersApiErrorBudgetBurnFast` (1h) и `FrontendErrorBudgetBurnFast` (1h) — pending |
| **1:33** | `PostgresDown` firing → **доставлен в sre-agent** |
| **3:20** | Оба fast-burn алерта firing → **доставлены в sre-agent** (доля плохих: 53% за 5m, 44% за 1h) |
| +0:03 после возврата | Сервис снова отвечает 200 |
| +0:50 | `PostgresDown` resolved → агенту |
| +5:41 | Fast-burn алерты resolved → агенту (короткое окно 5m «остыло») |

Агент получил оба симптома — причину (`PostgresDown`) и последствие (burn rate двух SLO). Связать их — задача
RALF-итерации в Фазе 7.

### 2. Все цели скрейпа живы, все запросы дашборда возвращают данные
34 цели — `up`; 18 запросов дашборда «Sandbox / Golden signals» — непустые (кроме 5xx на Traefik — их не было).

### 3. Мониторинг мониторинга
`AlertmanagerFailedToSendAlerts` загорелся сам: платформа ставится раньше приложений, и какое-то время
Alertmanager слал webhook'и агенту, которого ещё не было. Конвейер алертов следит за собой — и это сработало.
Аналогично всегда горящий `Watchdog` доказывает, что цепочка Prometheus → Alertmanager жива (маршрутизирован в `null`).

### 4. Медленные burn-rate алерты на молодом кластере
После 5-минутного отказа алерты по окнам 6h/1d/3d ушли в pending: при получасе истории 5 минут отказа — огромная доля
даже «трёхдневного» окна. Математика SLO предполагает, что окно заполнено данными; на свежем кластере длинные окна
сильно завышают долю ошибок, и warning-алерт может прийти через несколько часов после эксперимента.

### 5. Мелочи, найденные по дороге
| Что | Причина | Решение |
|---|---|---|
| `RequireHost("*:9090")` не защищал порт | Сравнивает **заголовок** `Host`, а не порт сокета | `UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 …)` |
| stderr orders-api: `libgssapi_krb5.so.2: cannot open shared object file` | Npgsql по умолчанию пробует Kerberos/GSSAPI-шифрование | `GssEncryptionMode = Disable` (orders-api 0.3.1) |
| 2 строки Information от HttpClient на каждый запрос frontend | Логирование `System.Net.Http.HttpClient` по умолчанию | Уровень `Warning` для этой категории |
| loadgen: `unknown flag: --no-summary` | k6 v2 переименовал флаг | `--summary-mode=disabled` |
| Grafana `Progress deadline exceeded`, Alloy качался 14 минут | Медленная загрузка образов (Grafana ~500 МБ) в каждую ноду отдельно | Pull-through кеши реестров ([registry-cache.ps1](../scripts/registry-cache.ps1)), см. [09](09-dev-environment.md) |

## Упражнения

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | Открыть Grafana → Sandbox / Golden signals, переключить `Service` | Трафик, ошибки, задержки, насыщение, burn rate, логи выбранного сервиса |
| 2 | Prometheus → Status → Targets; затем `up == 0` в Graph | Все цели и как выглядит упавшая |
| 3 | `histogram_quantile(0.99, sum by (le) (rate(http_server_request_duration_seconds_bucket{app="orders-api"}[5m])))` | p99 из гистограммы; попробовать без `sum by (le)` — почему результат бессмысленный |
| 4 | Grafana → Explore → Loki: `{namespace="sandbox", app="orders-api"} \| json \| LogLevel="Warning"` | Фильтрация структурных логов по полям |
| 5 | Повторить сквозной сценарий: `helm upgrade postgres infra/helm/postgres -n sandbox -f infra/helm/releases/sandbox/postgres.yaml --set replicas=0`, через 5 минут `./scripts/deploy-apps.ps1 -Services postgres` | Таймлайн как в п.1; смотреть Alertmanager UI и логи `sre-agent` |
| 6 | `POST /debug/allocate?mb=300` (с `X-Api-Key`) в под orders-api (Фаза 1) | `KubePodCrashLooping`/рестарты на дашборде, OOMKilled в kube-state-metrics |
| 7 | Создать Silence в Alertmanager UI на `PostgresDown` и повторить п.5 | Алерт горит в Prometheus, но не доходит до агента |
| 8 | Убрать `or vector(0)` из шаблона SLO (локально, `helm template`) и подумать, что случится при 100% ошибок | Пустой результат → нет алерта |
| 9 | Добавить `await Task.Delay(300)` в `GET /orders`, поднять тег в `orders-api.yaml` (например `0.3.2-slow`), `build-images` + `deploy-apps -Services orders-api`; потом откатить | Без единой 5xx сработает SLO — по латентности (порог 250 мс) |
| 10 | `kubectl port-forward -n sre-agent svc/sre-agent 8099:80`, затем `GET http://localhost:8099/alerts` | История алертов, которую увидит агент |

## Шпаргалка

```promql
up == 0                                                              # кто не скрейпится
sum by (app) (rate(http_server_request_duration_seconds_count{namespace="sandbox", http_route=~".+"}[5m]))   # rps
slo:sli_error:ratio_rate1h / 0.01                                    # burn rate за час
kube_pod_container_status_restarts_total{namespace="sandbox"}       # рестарты
pg_up                                                                # база жива?
```
```logql
{namespace="sandbox", level=~"Warning|Error"}                       # предупреждения и ошибки всех сервисов
{namespace="sandbox", app="orders-api"} |= "Database unavailable"   # поиск по тексту
```

| Симптом | Где смотреть | Частая причина |
|---|---|---|
| Сервиса нет в Targets | `kubectl get podmonitor,servicemonitor -A`; логи `prometheus-operator` | Selector не совпал с labels; нет порта `metrics`; `*SelectorNilUsesHelmValues` |
| Target `down` | Targets → Last Error | Неверный порт/путь; метрики слушают не на том порту |
| Правило не загрузилось | Status → Rules; `kubectl get prometheusrule -A` | Ошибка PromQL; Prometheus ещё не перечитал (config-reloader, ~минута) |
| Алерт горит, но не пришёл | Alertmanager UI → Silences/Inhibit; `AlertmanagerFailedToSendAlerts` | Маршрут в `null`, silence, недоступный webhook |
| Нет логов пода | `{pod="..."}` в Loki; поды Alloy на этой ноде | DaemonSet не на этой ноде (taint без toleration) |
| В SLO пустые значения | Запрос recording rule в Graph | Нет трафика (0/0 = NaN) — нужен loadgen |
