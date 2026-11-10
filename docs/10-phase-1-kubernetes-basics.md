# 10. Фаза 1: базовый Kubernetes на практике

Теория по каждому объекту — в [03-kubernetes-concepts.md](03-kubernetes-concepts.md).
Здесь — что именно развёрнуто, как это устроено и что можно потрогать руками.

> **С Фазы 3 эти сырые манифесты заменены Helm-чартом** ([12-phase-3-helm.md](12-phase-3-helm.md)).
> Сами файлы остались в истории git — смотреть их так же поучительно:
> `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml` (а также `frontend.yaml`, `worker.yaml`, `00-namespace.yaml`).
> Чтобы повторить фазу «руками», можно применить их на чистом кластере:
> `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml | kubectl apply -f -`.
> Объекты и поведение в кластере те же, поэтому упражнения ниже актуальны.

## Что развёрнуто

```
 kubectl port-forward ──► Service frontend (ClusterIP :80)
                               │ выбирает поды по label app.kubernetes.io/name=frontend
                               ▼
                         frontend × 2 ──HTTP + X-Api-Key──► Service orders-api (:80)
                                                                 ▼
                                                           orders-api × 3   (заказы в памяти пода!)
                                                                 ▲
                         worker × 1 ──каждые 5 с: pending? → process──┘   (у worker нет Service)
```

| Объект | Файл | Чему учит |
|---|---|---|
| Namespace `sandbox` + Pod Security `restricted` | [namespace-sandbox.yaml](../infra/manifests/platform/namespace-sandbox.yaml) | Изоляция; API-сервер не пустит под, запущенный от root |
| Deployment ×3, Service ×2 | `frontend.yaml`, `orders-api.yaml`, `worker.yaml` | Реплики, rolling update, стабильное DNS-имя |
| ConfigMap → **env** | `frontend-config`, `worker-config` | Значение читается один раз при старте контейнера |
| ConfigMap → **файл** | `orders-api-config` | Файл обновляется в живом поде (~1 мин) |
| Secret → env и → файл | `sandbox-api-key` (создаёт скрипт) | Секреты не в git; два способа подключения |
| Probes, requests/limits, securityContext | все Deployment'ы | Самовосстановление, планирование, OOMKilled, безопасность |

Код сервисов — `src/Sandbox.Services/`, один общий [Dockerfile](../src/Sandbox.Services/Dockerfile) (multi-stage).

## Развернуть

```powershell
./scripts/cluster-up.ps1        # если кластера ещё нет
./scripts/build-images.ps1      # docker build + kind load для 3 образов
./scripts/deploy-infra.ps1      # namespace, Traefik, cert-manager (с Фазы 3)
./scripts/deploy-apps.ps1       # secret + Helm-релизы сервисов (с Фазы 3)
kubectl port-forward -n sandbox svc/frontend 8080:80
```

```powershell
Invoke-RestMethod -Method Post "http://localhost:8080/order?item=book"
Invoke-RestMethod http://localhost:8080/
```

Чтобы понять механику, сделайте хотя бы раз вручную то, что делал скрипт Фазы 1 (на чистом кластере):
`kubectl apply -f infra/manifests/platform/namespace-sandbox.yaml`, затем
`kubectl create secret generic sandbox-api-key -n sandbox --from-literal=api-key=<что-нибудь>`,
затем `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml | kubectl apply -f -` (и так для `frontend.yaml`, `worker.yaml`)
— и смотреть `kubectl get pods -n sandbox -w`.

## Разбор ключевых мест в манифестах

**`selector` и `labels`.** Deployment и Service «находят» свои поды только по меткам.
`selector` Deployment'а неизменяем после создания, поэтому в нём только стабильная метка
`app.kubernetes.io/name`, а `app.kubernetes.io/version` — только в шаблоне пода.

**Тег образа `0.1.0`, а не `latest`.** Для `:latest` Kubernetes по умолчанию ставит
`imagePullPolicy: Always` и пытается скачать образ из реестра — в kind его там нет → `ErrImagePull`.

**`maxUnavailable: 0, maxSurge: 1`** (orders-api). При обновлении сначала поднимается новый под,
и только когда он Ready, гасится старый — ёмкость не падает ниже 3.

**`topologySpreadConstraints`.** Просим раскладывать реплики orders-api по разным нодам.
`ScheduleAnyway` — мягкое требование: если не получается, под всё равно запустится.

**Probes.** `startupProbe` даёт до 60 с на старт; потом работают `liveness` (перезапуск при зависании)
и `readiness` (вывод из балансировки). У worker liveness проверяет, **крутится ли его цикл**, а не
доступен ли orders-api: иначе падение orders-api вызывало бы бессмысленные рестарты worker'а.

**securityContext.** `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: [ALL]`, `seccompProfile` —
ровно то, что требует профиль `restricted`. Корень ФС только для чтения, поэтому `/tmp` — отдельный `emptyDir`.

**Secret не в git.** Скрипт ([deploy-apps.ps1](../scripts/deploy-apps.ps1) с Фазы 3) генерирует случайный ключ,
если Secret'а ещё нет. Позже можно заменить на Sealed Secrets, чтобы секреты хранились в репозитории зашифрованными.

## Что мы обнаружили при проверке (и почему это важно)

Эти эффекты реально наблюдались при первом запуске — это не теория.

### 1. «Балансировка» не балансирует
Все запросы шли в **один** под frontend и **один** под orders-api. Причины две:
- `kubectl port-forward svc/frontend` выбирает один под и держит туннель к нему — Service тут не участвует.
- HttpClient держит **keep-alive** соединение, а kube-proxy балансирует **соединения (L4)**, а не запросы.
  Один раз выбранный под получает все последующие запросы по этому соединению.

В проде с этим борются L7-балансировкой по запросам — именно это даёт service mesh (Фаза 6) или Ingress (Фаза 2).

### 2. У каждой реплики — своё состояние
Заказы хранятся в памяти пода. После 4 созданных заказов:
```
orders-api-...-ppqzn -> 0 orders
orders-api-...-qth9b -> 4 orders
orders-api-...-zjrjt -> 0 orders
```
а worker опрашивал `ppqzn` и честно писал `Polled orders-api-...-ppqzn: 0 pending`. Плюс после любого
рестарта или rollout все заказы исчезают. Вывод: **stateless-реплики не должны хранить состояние** —
отсюда Фаза 4 (PostgreSQL на StatefulSet + PVC).

### 3. .NET сам защищается от лимита памяти
Первая версия `/debug/allocate` выделяла управляемые массивы — и вместо OOMKilled получила HTTP 500
(`OutOfMemoryException`). Runtime .NET знает о лимите контейнера и ограничивает кучу GC (~75% от лимита).
Поэтому эндпоинт сейчас выделяет **native**-память, которую GC не контролирует, — и тогда ядро убивает
контейнер: `lastState=OOMKilled`. В реальности OOMKilled у .NET-сервисов чаще всего означает утечку
неуправляемой памяти или слишком маленький лимит относительно нагрузки.

### 4. ConfigMap-файл обновился за 70 секунд
После `kubectl patch configmap orders-api-config` значение в `/info` сменилось через ~70 с, рестартов 0.
Kubelet синхронизирует тома с периодом около минуты. Для env-переменных (frontend) так не будет никогда —
нужен `kubectl rollout restart`.

## Упражнения

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | `kubectl delete pod -n sandbox <orders-api-pod>` и `kubectl get pods -n sandbox -w` | Почти сразу новый под с другим именем; ReplicaSet держит 3 |
| 2 | `kubectl scale deploy/orders-api -n sandbox --replicas=5`, затем обратно 3 | Где оказались новые поды: `-o wide` |
| 3 | Создать пару заказов и открыть каждый под orders-api через `kubectl port-forward pod/<имя> 8081:8080` (`GET /orders` с заголовком `X-Api-Key`) | Заказы только на одном поде (эффект 2) |
| 4 | Поменять `Frontend__Greeting` через `kubectl edit configmap frontend-config -n sandbox` | Ответ не меняется, пока не сделать `kubectl rollout restart deploy/frontend -n sandbox` |
| 5 | Поменять `Banner` в `orders-api-config`, подождать ~1 мин, `GET /info` у пода | Меняется без рестарта (эффект 4) |
| 6 | `kubectl get secret sandbox-api-key -n sandbox -o jsonpath='{.data.api-key}'` и декодировать base64 | Секрет — это **не шифрование**, а base64; защищает только RBAC |
| 7 | Через port-forward к поду orders-api: `POST /debug/allocate?mb=300` с заголовком `X-Api-Key` (с ревью 2026-11-10 эндпоинт под ключом, `mb` от 1 до 512) | `kubectl describe pod` → `Last State: Terminated, Reason: OOMKilled`, `Restart Count: 1` |
| 8 | «Плохой релиз»: `kubectl set image deploy/orders-api orders-api=sandbox/orders-api:0.9.9 -n sandbox` | Новый под в `ErrImagePull`/`ImagePullBackOff`, старые 3 продолжают работать (спасибо `maxUnavailable: 0`). Откат: `kubectl rollout undo deploy/orders-api -n sandbox` |
| 9 | `kubectl rollout history deploy/orders-api -n sandbox` | История ревизий — то, что позже будет использовать агент для rollback |
| 10 | Удалить Secret: `kubectl delete secret sandbox-api-key -n sandbox`, затем удалить под frontend | Новый под в `CreateContainerConfigError` — Secret нужен на старте. Вернуть: `./scripts/deploy-apps.ps1` |
| 11 | `docker stop sre-ai-worker2`, подождать ~5 мин | Нода `NotReady`, поды на ней через какое-то время пересоздаются на других нодах. Почему так долго — см. `tolerations` для `node.kubernetes.io/unreachable` в `kubectl get pod -o yaml` |

## Шпаргалка отладки

| Симптом | Где смотреть | Частая причина |
|---|---|---|
| `Pending` | `kubectl describe pod` → Events | Не хватает ресурсов под requests, не подходит ни одна нода |
| `ErrImagePull` / `ImagePullBackOff` | Events | Нет образа в kind (`kind load`), тег `latest`, опечатка |
| `CreateContainerConfigError` | Events | Нет ConfigMap/Secret или ключа в нём |
| `CrashLoopBackOff` | `kubectl logs <pod> --previous` | Приложение падает при старте |
| `OOMKilled` | `kubectl describe pod` → Last State | Превышен лимит памяти |
| Running, но не Ready | `kubectl describe pod` → readiness failures | Readiness probe не проходит |
| Под не получает трафик | `kubectl get endpointslices -n sandbox` | Labels пода не совпадают с selector Service |
| Нарушение Pod Security | Ответ `kubectl apply` / events ReplicaSet | Нет `runAsNonRoot`, `seccompProfile` и т.п. |

Универсальное правило: `kubectl get events -n sandbox --sort-by=.lastTimestamp` — почти всегда ответ там.
