# 15. Фаза 6: безопасность сети — NetworkPolicy, mTLS, service mesh

Теория — разделы «NetworkPolicy», «TLS и mTLS», «Service Mesh» в [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Два слоя защиты

| Слой | Чем | Что знает о клиенте | Что даёт |
|---|---|---|---|
| L3/L4 | **NetworkPolicy** (исполняет CNI — kindnet) | IP пода → его **метки** | «кто с кем вообще может соединиться» |
| L7 | **Linkerd** (sidecar-прокси в каждом поде) | **Криптографическая identity** из mTLS-сертификата (ServiceAccount) | шифрование, identity, метрики каждого запроса, балансировка по запросам, retries, timeouts |

Они дополняют друг друга, и проверка показала почему (см. «Что обнаружили», п.2).

## NetworkPolicy: default-deny + явные разрешения

**Базовый уровень** — [networkpolicy-baseline.yaml](../infra/manifests/platform/networkpolicy-baseline.yaml)
для `sandbox` и `sre-agent`: запретить всё входящее и исходящее, разрешить DNS (без него не резолвится ни одно
имя — самый частый способ «положить» кластер default-deny), разрешить прокси Linkerd ходить к control plane и
отдавать метрики (4191) и tap (4190).

**Каждый сервис в своих values** объявляет, кто его вызывает и кого вызывает он:

```
                      ┌──────────── traefik (ns) ─────────────┐
                      ▼                                       ▼
loadgen ──► traefik   frontend ──────────► orders-api ◄────── worker
(только 443)          (from: traefik;      (from: frontend, worker, traefik;
                       to: orders-api)      to: postgres)
                                               │
                                               ▼
                                            postgres (from: orders-api; to: никого)
monitoring (ns) ──► :metrics, :4191 всех подов          sre-agent (from: под alertmanager — webhook)
```

```yaml
# infra/helm/releases/sandbox/orders-api.yaml
networkPolicy:
  allowFromApps: [frontend, worker]
  allowFromNamespaces: [traefik]
  allowToApps:
    - app: postgres
      port: postgres
```

Детали, которые легко упустить:
- **`allowFromNamespaces` пускает весь namespace, `allowFromPods` — один под в нём** (`{ namespace: monitoring, app:
  alertmanager }`: `namespaceSelector` и `podSelector` в одном элементе означают «и то, и другое»). Агенту нужен второй
  вариант: у его API есть `POST /approvals` и `POST /runs`, а `monitoring` — privileged namespace с кучей чужих подов.
- **Порт — это порт пода, а не Service.** Политика видит пакет после DNAT Service: `orders-api:80` → под `:8080`.
  Поэтому в правилах — **имена** портов контейнера (`http`, `postgres`), а не 80.
- **Политики складываются.** Разрешено, если разрешает *хотя бы одна* политика, выбирающая под. Запрещающих правил нет —
  «запрет» = отсутствие разрешения при наличии хоть одной политики, выбирающей под.
- **Под `helm test` получил свою метку** (`<release>-test`) и свою политику: с меткой приложения он подпадал бы под его правила.
- **Kubelet probes** проходят всегда: трафик с собственной ноды пода политиками не ограничивается.
- **NetworkPolicy работает, только если её исполняет CNI.** Проверили отдельно: без политики `200`, с deny-all — таймаут.

## Linkerd

### Почему Linkerd, и какой
Linkerd проще и легче Istio — для обучения mesh это хороший старт. Но **с февраля 2024 открытый проект публикует только
edge-релизы**; stable-сборки выпускают вендоры (Buoyant Enterprise) ([releases](https://linkerd.io/releases/)).
Ставим `edge-26.11.1` (Helm-чарты `2026.11.1` из `helm.linkerd.io/edge`). Третий раз в проекте (после ingress-nginx и
Bitnami) жизненный цикл зависимости определяет выбор — это нормальная часть работы SRE.

### Что развёрнуто

| Компонент | Namespace | Роль |
|---|---|---|
| `linkerd-crds` (+ Gateway API CRD) | `linkerd` | Server, AuthorizationPolicy, HTTPRoute… |
| `linkerd-cni` (DaemonSet) | `linkerd-cni` | Перенаправляет трафик пода в прокси на этапе настройки сети пода |
| `linkerd-control-plane` | `linkerd` | identity (сертификаты прокси), destination (discovery, балансировка), policy, proxy-injector |
| `linkerd-viz` | `linkerd-viz` | Дашборд и tap поверх **нашего** Prometheus |
| PodMonitor `linkerd-proxy` | `monitoring` | Скрейп метрик всех прокси ([podmonitor-linkerd.yaml](../infra/helm/sandbox-observability/templates/podmonitor-linkerd.yaml)) |

```powershell
kubectl port-forward -n linkerd-viz svc/web 8084:8084    # http://localhost:8084
```

### Identity: цепочка сертификатов через cert-manager

```
linkerd-trust-anchor (корень, 10 лет)  ──подписывает──►  linkerd-identity-issuer (промежуточный CA, 48 ч, cert-manager продлевает)
                                                            └─► linkerd-identity выдаёт каждому прокси сертификат на 24 ч
                                                                 с именем <serviceaccount>.<namespace>.serviceaccount.identity.linkerd.cluster.local
```

[linkerd-identity.yaml](../infra/manifests/platform/linkerd-identity.yaml). Отдельный корень от CA для Ingress: разные домены
доверия не должны делить корень. Ротация промежуточного сертификата и сертификатов прокси — автоматическая.

Что потребовалось на деле: при `identity.externalCA: true` чарт **не создаёт** ConfigMap `linkerd-identity-trust-roots`
(поды control plane висели в `CreateContainerConfigError`) — он ожидает, что корень раздаст внешний инструмент. В проде это
**trust-manager** (проект cert-manager), который умеет держать в bundle и старый, и новый корень во время ротации корня.
У нас ConfigMap создаёт [deploy-infra.ps1](../scripts/deploy-infra.ps1) из секрета cert-manager. И ещё: в проде приватный
ключ корня не хранят в кластере вовсе (офлайн-CA, Vault, KMS).

### Совместимость с Pod Security `restricted`
Обычная инъекция добавляет init-контейнер с `NET_ADMIN`/`NET_RAW` (он пишет iptables) — профиль `restricted` такое
запрещает. **linkerd-cni** делает то же самое на уровне ноды, когда создаётся сеть пода, и в поде остаются только
непривилегированные контейнеры. Проверка: поды в `sandbox` приняты API-сервером с прокси внутри.

### Native sidecar
Прокси внедряется как *native sidecar* — init-контейнер с `restartPolicy: Always` (в `kubectl get pod` он среди initContainers).
Он стартует **до** приложения и останавливается **после** него — иначе `preStop`-задержка приложения из Фазы 3 работала бы
впустую: прокси перед ним уже не принимал бы соединений.

### Как включается mesh
Аннотация `linkerd.io/inject: enabled` на namespace (`sandbox`, `sre-agent`) — чарты приложений не меняются. Существующие поды
получают прокси только после пересоздания (`kubectl rollout restart`). Traefik и мониторинг в mesh **не** входят (упражнение 3).

## Retries и timeouts: Gateway API HTTPRoute

[httproutes.yaml](../infra/helm/sandbox-service/templates/httproutes.yaml), включено для orders-api (`mesh.routes.enabled`):

| HTTPRoute | Совпадение | Политика |
|---|---|---|
| `orders-api-reads` | `method: GET` | до 3 повторов на 5xx, 1 с на попытку, 3 с на весь запрос |
| `orders-api-all` | всё остальное | только timeout 3 с, **без повторов** |

- Родитель маршрута — **Service**, а не Gateway (подход GAMMA для трафика «сервис → сервис»).
- Повторы делает прокси **вызывающего** (frontend, worker). Запросы от Traefik (вне mesh) ничего этого не получают.
- **`POST` не повторяется**: повтор создания заказа = дубль заказа. Повторять можно только идемпотентное.
- Как только у Service есть хоть один HTTPRoute, Linkerd пропускает **только** описанные маршруты — отсюда обязательный catch-all.

## Что обнаружили при проверке

### 1. NetworkPolicy реально блокирует
Временные поды-«самозванцы»:
```
[sandbox] as 'frontend' to postgres:5432       -> blocked
[sandbox] as 'worker'   to frontend:80         -> blocked
[sandbox] as 'loadgen'  to orders-api:80       -> blocked
[sandbox] as 'nobody'   to orders-api:80       -> blocked
[default] as 'nobody'   to orders-api.sandbox  -> blocked
[sandbox] as 'worker'   to orders-api:80       -> CONNECTED   (контроль)
[sandbox] as 'orders-api' to postgres:5432     -> CONNECTED   (контроль)
```

### 2. NetworkPolicy доверяет меткам — и это дыра
Под с меткой `app.kubernetes.io/name: worker` получил права worker'а, **не будучи им**. Любой, кто может создать под в
namespace, может назначить ему любую метку. mTLS закрывает это: identity — это сертификат на ServiceAccount, подделать
его меткой нельзя. Для этого каждому сервису выдан **свой ServiceAccount** (раньше все были `default`), без токена API.

### 3. mTLS и identity видны в метриках прокси
```
orders-api <- frontend.sandbox.serviceaccount.identity.linkerd.cluster.local   tls=true   4.63 rps
orders-api <- worker.sandbox.serviceaccount.identity.linkerd.cluster.local     tls=true   1.13 rps
postgres   <- orders-api.sandbox.serviceaccount.identity.linkerd.cluster.local tls=true   port=5432 open=4
frontend   <- (none)                                                           no_identity  ← Traefik вне mesh
```
Под `helm test` виден как `default.sandbox.serviceaccount…` — у него нет своего ServiceAccount.

### 4. Ловушка: весь трафик между прокси идёт на порт 4143
После инъекции orders-api перестал видеть базу (`/health/db: Unhealthy`, `GET /` → 502). 5432 — один из «opaque»-портов
Linkerd по умолчанию: прокси клиента отправляет поток не на 5432, а на **входной порт прокси** сервера — 4143. Политика
пускала только 5432. Затем то же проявилось и для HTTP: тестовый под в mesh не достучался до orders-api, хотя 8080 был
разрешён, — в логе прокси `endpoint{addr=…:8080}: Failed to connect`, а реальное соединение шло на 4143.

Решение — открыть 4143 **тем же** парам «клиент → сервер», что уже разрешены. Цена: кто достучался до 4143, через прокси
доберётся до любого порта пода. С этого момента тонкий контроль доступа — задача identity-based авторизации Linkerd
(`Server` + `AuthorizationPolicy`, упражнение 5), а NetworkPolicy остаётся грубым внешним периметром.

### 5. Балансировка по запросам — эффект Фазы 1 исчез
В Фазе 1 каждый под frontend «прилипал» к одному поду orders-api (kube-proxy балансирует соединения). Теперь:
```
orders-api-…-hpcs6: 1.25 rps   orders-api-…-x98ht: 1.71 rps   orders-api-…-xn4d8: 1.69 rps   (только от frontend)
```
Прокси Linkerd выбирает под на **каждый запрос** (EWMA по задержке) поверх одного HTTP/2-соединения.

### 6. Жёсткое убийство пода — 0 ошибок даже без retries
`kubectl delete pod --grace-period=0 --force` трижды под нагрузкой: **104 из 104** запросов успешны. Отказ соединения с
мёртвым endpoint'ом прокси обрабатывает до отправки запроса и выбирает другой под, а destination-контроллер быстро убирает
под из выдачи. Без mesh аналогичный сценарий в Фазе 3 терял запросы.

### 7. Retries: что они могут и чего нет
Рестарт PostgreSQL (около 3 с недоступности) под нагрузкой, по 2 прогона:

| | Ошибок у пользователя |
|---|---|
| без retries | 3, 5 |
| с retries (GET, до 3 повторов) | 2, 2 |

Метрики прокси frontend за тот же период: `retry_requests` 103, `retry_successes` 12, `retry_limit_exceeded` 19.
Повторы сглаживают короткие «моргания», но не многосекундный отказ — они идут сразу, без паузы. И они **умножают нагрузку**:
до 4 запросов к и так больному сервису на каждый пользовательский. Это механика *retry storm*; поэтому — небольшой лимит,
только идемпотентные запросы и retry budget.

### 8. С нуля — 9 минут
Пересоздание кластера и полный прогон `cluster-up → build-images → deploy-infra → deploy-apps`: **8 мин 59 с**
(с прогретыми кешами образов), все поды сразу в mesh, `helm test` всех трёх сервисов проходит. Порядок важен:
cert-manager → identity Linkerd → CRD → CNI → control plane — и только потом приложения, иначе поды создадутся без прокси.

### 9. Мелочи по дороге
| Что | Причина | Решение |
|---|---|---|
| `helm --set-file` не нашёл файл `E:Gitpet-projects…` | Helm разбирает `\` в пути как escape | Прямые слэши в пути |
| Схема отвергла `getRetries.true` | YAML 1.1 читает ключ `on:` как `true` | Ключ `retryOn` |
| 1 таймаут из 106 при включении политик | Пересоздание подов (сменился ServiceAccount) | — |
| Кластер «пропал» после перезагрузки ПК | Docker Desktop не был запущен | Запустить Docker: ноды kind и кеши поднимаются сами |

## Упражнения

**Снимок проекта на момент этой фазы** — тег `phase-6` (коммит `2739c5a`). Повторным прогоном не проверялся —
это состояние, на котором упражнения были составлены.

```powershell
git worktree add ../sre-ai-phase-6 phase-6   # отдельная папка; ваша рабочая копия не меняется
cd ../sre-ai-phase-6
./scripts/cluster-down.ps1                   # имя кластера общее (sre-ai): сначала удалить текущий
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... упражнения ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-6 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # собрать текущий проект заново
```

**Предусловия и откат.** Нужен зелёный `./scripts/smoke-test.ps1`. Упражнение 3 меняет values Traefik
(платформа), 5–6 создают `Server`/`AuthorizationPolicy` (скрипт их удаляет — в baseline их нет). Откат:

```powershell
git restore infra/
./scripts/reset-exercise.ps1 -Level Platform
```

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | Открыть Linkerd Viz (port-forward выше): Namespaces → sandbox → orders-api | Success rate, RPS, задержки по каждому ребру, mTLS-замки |
| 2 | `kubectl get pod <orders-api-pod> -n sandbox -o yaml`: найти `linkerd-proxy` в initContainers, `restartPolicy: Always`, том с токеном identity | Как выглядит внедрённый прокси |
| 3 | Включить mesh для Traefik (`podAnnotations: linkerd.io/inject: ingress` в values Traefik + egress/ingress 4143) | `frontend <- traefik.traefik.serviceaccount… tls=true`: шифрование на всём пути |
| 4 | Убрать `- port: 4143` из политики postgres (`helm template` + `kubectl apply` на копии) | Снова `Database unavailable` — ловушка п.4 |
| 5 | Создать `Server` (порт `http` orders-api) и `AuthorizationPolicy` с `MeshTLSAuthentication` только для `frontend` | worker получит 403 от прокси — авторизация по identity, которую не подделать меткой |
| 6 | Под-«самозванец» с меткой worker (как в п.1) против orders-api после упражнения 5 | Соединение есть (NetworkPolicy пустила), запрос — нет (identity не та) |
| 7 | Выключить retries (`getRetries.enabled: false`) и повторить рестарт базы | Цифры из п.7 |
| 8 | `kubectl get certificate -n linkerd`, `kubectl get secret linkerd-identity-issuer -n linkerd -o yaml` | Срок 48 ч; cert-manager продлевает — Linkerd подхватывает без рестарта |
| 9 | Сравнить с Istio ambient mode (без sidecar, ztunnel на ноде + waypoint для L7) — почитать, не ставить | Чем модель «прокси в каждом поде» отличается от «прокси на ноде» |

## Шпаргалка

```promql
sum by (deployment, client_id, tls) (rate(request_total{direction="inbound", namespace="sandbox"}[2m]))   # кто кого и с mTLS ли
sum by (dst_deployment) (rate(request_total{direction="outbound", deployment="frontend"}[2m]))          # куда ходит frontend
sum by (deployment, route_name) (increase(outbound_http_route_retry_requests_total[5m]))               # повторы
```

| Симптом | Где смотреть | Частая причина |
|---|---|---|
| Всё перестало резолвиться после default-deny | `nslookup` из пода | Нет разрешения на DNS (UDP+TCP 53 к kube-dns) |
| Сервис не достаёт соседа, но порт в политике открыт | логи `linkerd-proxy` клиента: `Failed to connect` | Не открыт 4143 для трафика между прокси |
| Под не создаётся: `violates PodSecurity "restricted"` | события ReplicaSet | Инъекция без linkerd-cni (init-контейнер с `NET_ADMIN`) |
| Control plane в `CreateContainerConfigError` | `kubectl describe pod -n linkerd` | Нет ConfigMap `linkerd-identity-trust-roots` при `externalCA` |
| Запросы к Service внезапно 404 | `kubectl get httproute -n sandbox` | Есть HTTPRoute, но нет маршрута под этот запрос |
| В метриках `tls="no_identity"` | Метки client_id | Клиент вне mesh (Traefik, Prometheus, kubelet) |
