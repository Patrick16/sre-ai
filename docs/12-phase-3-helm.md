# 12. Фаза 3: Helm на практике

Теория — раздел «Helm» в [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Какие проблемы Фаз 1–2 решает Helm

| Было (сырые манифесты) | Стало (чарт) |
|---|---|
| Три почти одинаковых YAML по ~150 строк: securityContext, probes, тома копировались руками | Один шаблон [`sandbox-service`](../infra/helm/sandbox-service/), различия — в 20–40 строках values на сервис |
| Сменил env в ConfigMap — поды не узнают, нужен ручной `rollout restart` | Checksum ConfigMap в шаблоне пода → изменение конфига само запускает rolling update |
| Что сейчас задеплоено и что было до этого — неизвестно | `helm history`: ревизии, версии чарта, статус; `helm rollback` одной командой |
| Опечатка в поле молча игнорируется | `values.schema.json` отклоняет неизвестные поля и тег `latest` до похода в кластер |
| Нет проверки «работает ли после деплоя» | `helm test` запускает под, который стучится в сервис по его DNS-имени |

## Устройство

```
infra/helm/
├── sandbox-service/            ← НАШ чарт: один на все сервисы
│   ├── Chart.yaml              version (чарта) и appVersion (тег образа по умолчанию)
│   ├── values.yaml             значения по умолчанию + комментарии «зачем»
│   ├── values.schema.json      валидация values
│   └── templates/
│       ├── _helpers.tpl        имена, labels, selectorLabels, рендер probe
│       ├── deployment.yaml     securityContext, probes, тома, preStop, checksum
│       ├── service.yaml        если service.enabled
│       ├── configmap-env.yaml  configEnv → envFrom
│       ├── configmap-files.yaml configFiles → файлы
│       ├── ingress.yaml        Ingress (+ Traefik Middleware, если stripPrefix)
│       └── tests/test-health.yaml  hook для `helm test`
├── releases/sandbox/           ← values КАЖДОГО релиза: frontend.yaml, orders-api.yaml, worker.yaml
└── values/                     ← values для чартов сообщества: traefik.yaml, cert-manager.yaml
```

**Слои values** (каждый следующий перекрывает предыдущий):
`sandbox-service/values.yaml` → `releases/sandbox/<service>.yaml` (`-f`) → `--set` в командной строке.

Посмотреть, что получится, **не трогая кластер**:
```powershell
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml
```

### Один чарт — три релиза (а не umbrella-чарт)
Можно было сделать один «зонтичный» чарт `sandbox` с тремя зависимостями — тогда `helm rollback sandbox`
откатывал бы всё разом. Выбрали **отдельный релиз на сервис**: так устроены настоящие микросервисы —
каждый выкатывается и откатывается независимо, у каждого своя история. Цена: нет атомарного деплоя всей системы.

### Платформа и приложения
| Слой | Что | Скрипт |
|---|---|---|
| Платформа | Namespace с Pod Security, Traefik, cert-manager, CA-issuers ([manifests/platform/](../infra/manifests/platform/)) | [deploy-infra.ps1](../scripts/deploy-infra.ps1) |
| Приложения | Secret (генерируется), три Helm-релиза | [deploy-apps.ps1](../scripts/deploy-apps.ps1) |

Namespace не создаётся чартом приложения намеренно: `helm --create-namespace` не умеет ставить labels
(Pod Security), а namespace переживает любой отдельный релиз. В компаниях это зона «платформенной команды».

## Ключевые приёмы в шаблонах

- **Selector — только `app.kubernetes.io/name`.** Стандартные чарты добавляют в selector ещё
  `app.kubernetes.io/instance`, но selector Deployment'а неизменяем: при миграции существующих объектов это
  означало бы удаление и пересоздание. Метки `version`, `helm.sh/chart` — только в `labels`, не в selector.
- **`checksum/config-env`** — sha256 от отрендеренного ConfigMap в аннотации шаблона пода. Изменился конфиг →
  изменился шаблон пода → новый ReplicaSet. Для `configFiles` checksum нет: orders-api перечитывает файл сам.
- **`lifecycle.preStop.sleep`** (`shutdownDelaySeconds: 5`) — см. «Что обнаружили», пункт 3.
- **`required` и `fail`** — шаблон падает с понятной ошибкой (`ingress.host is required`), а не рендерит мусор.
- **Middleware только если `stripPrefix`** — Traefik-специфика изолирована в одном `if`.
- **Hook `helm.sh/hook: test`** — под создаётся только командой `helm test`, а не при деплое.

## Как мы мигрировали живые ресурсы (и что сломалось по дороге)

Сервисы уже работали из `kubectl apply`. Цель — передать их Helm **без простоя**. Вот что произошло на деле:

**Попытка 1 — просто `helm upgrade --install`:**
```
Service "orders-api" ... exists and cannot be imported into the current release: invalid ownership metadata;
missing key "app.kubernetes.io/managed-by": must be set to "Helm" ... "meta.helm.sh/release-name" ...
```
Helm не захватывает ресурсы без своих меток — защита от того, чтобы два релиза не перетирали друг друга.

**Попытка 2 — `--take-ownership`** (новый флаг Helm 4):
```
Apply failed with 2 conflicts: conflicts with "kubectl-client-side-apply":
- ...volumeMounts[mountPath="/app/config"].name
```
Helm 4 по умолчанию использует **server-side apply (SSA)**: API-сервер помнит, *какой менеджер владеет каким
полем* (`metadata.managedFields`). Поля, которые меняют значение, принадлежат `kubectl` — конфликт.

**Попытка 3 — `--take-ownership --force-conflicts`:** успех, простоя нет (14 из 15 запросов — 200, см. пункт 3 ниже).

**Мина замедленного действия.** Следующий же `helm upgrade` с новым тегом образа снова упал с конфликтом —
по полям `image` и `version`. `--force-conflicts` забрал только поля, которые **тогда** различались; поля с
одинаковыми значениями остались в **совместном** владении `helm` и `kubectl-client-side-apply`. Правильное
лечение — не `--force-conflicts` в каждом деплое, а удалить старого менеджера:

```powershell
kubectl get deploy orders-api -n sandbox -o json --show-managed-fields   # managedFields по умолчанию скрыты
# убрать из managedFields записи manager=kubectl-client-side-apply (JSON patch replace /metadata/managedFields)
# и аннотацию kubectl.kubernetes.io/last-applied-configuration (её использует клиентский kubectl apply)
```
После этого у полей один владелец — `helm` (и `kube-controller-manager` у статуса), и все последующие
`upgrade`/`rollback` прошли без флагов.

**Сироты.** Переименованные объекты (`orders-api-config` → `orders-api-files`, Middleware `strip-api` →
`orders-api-strip-prefix`) Helm не удаляет — он про них не знает. Их пришлось удалить руками.

На свежем кластере ничего этого нет: ресурсы сразу создаёт Helm. Проверено пересозданием кластера с нуля —
**2 мин 41 с** от `cluster-up` до работающего HTTPS, у всех объектов менеджеры только `helm` и контроллеры.

## Что обнаружили при проверке

### 1. Выпуск и откат — без единой ошибки
Под непрерывным опросом обоих маршрутов (каждые 200 мс): `upgrade` orders-api на 0.2.0 → `rollback` → смена
конфигурации frontend → возврат. **128 из 128** ответов — 200.
```
rev 5  Rollback to 3
rev 6  Upgrade complete   (0.2.0)
rev 7  Rollback to 5      (снова 0.1.0)
```
Обратите внимание: rollback — это **новая ревизия** с содержимым старой, история не переписывается.

### 2. Checksum работает, но «rollout завершён» ≠ «весь трафик на новой версии»
Сразу после `helm upgrade --wait` Deployment был полностью обновлён (`updated=2 ready=2`), но первый запрос
вернул **старое** приветствие. Старые поды ещё 5 секунд доживают свой `preStop` и могут получить запрос.
Через пару секунд — только новое. Похожий эффект с другой стороны: на свежем кластере сразу после деплоя
`GET /` вернул **503** — поды уже Ready, но Traefik ещё не получил обновлённые EndpointSlices.
**Kubernetes и прокси видят мир с задержкой** — это нормально, но это надо учитывать в проверках после деплоя.

### 3. preStop и потеря запросов при rolling update
Во время миграции orders-api один запрос из 15 упал (`000`), хотя `maxUnavailable: 0`. Причина — гонка при
остановке пода: kubelet шлёт SIGTERM, Kestrel перестаёт принимать соединения, а Traefik ещё не узнал, что под
уходит, и шлёт ему запрос. Лечение — `preStop: sleep 5`: под ещё 5 секунд работает, пока изменения endpoints
расходятся по кластеру, и только потом получает SIGTERM.

Интересная деталь: **rollout, который добавил preStop, всё равно потерял запросы** (2 из 28) — потому что
гасил *старые* поды, созданные без preStop. Защита работает только для подов, созданных уже с ней. Следующий
`rollout restart` — **47 из 47**.

## Отличия Helm 4, на которые наткнулись

| Helm 3 | Helm 4 |
|---|---|
| `--atomic` | `--rollback-on-failure` |
| `--force` | `--force-replace` |
| client-side 3-way merge | server-side apply по умолчанию (`--server-side auto`) |
| — | `--take-ownership` для захвата существующих ресурсов |
| `--wait` по готовности | `--wait` = стратегия `watcher` (kstatus) |

## Опасность `--set`

`helm upgrade ... --set image.tag=0.2.0` работает, но значение **живёт только в кластере** (`helm get values`),
а не в git. Следующий деплой из values-файла молча вернёт старый тег. В проде версии меняют коммитом в
values-файл; следующий шаг — GitOps (Argo CD, Фаза 10), где кластер сам синхронизируется с git.

## Упражнения

**Снимок проекта на момент этой фазы** — тег `phase-3` (коммит `50ed554`). Заказы там ещё в памяти подов
(PostgreSQL появился в Фазе 4). Повторным прогоном не проверялся — это состояние, на котором упражнения были
составлены.

```powershell
git worktree add ../sre-ai-phase-3 phase-3   # отдельная папка; ваша рабочая копия не меняется
cd ../sre-ai-phase-3
./scripts/cluster-down.ps1                   # имя кластера общее (sre-ai): сначала удалить текущий
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... упражнения ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-3 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # собрать текущий проект заново
```

**Предусловия и откат.** Нужен зелёный `./scripts/smoke-test.ps1`. Для упражнения 1 нужен git-коммит `8a47ee6`
(он в истории). Упражнения 3–9 правят values-файлы и создают drift. Откат (включая конфликт владельца полей
после `kubectl scale` из п. 8 — скрипт берёт поля обратно через `--force-conflicts`):

```powershell
git restore infra/helm/releases                  # если правили values-файлы
./scripts/reset-exercise.ps1
```

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | `helm template` для каждого сервиса и сравнить с удалёнными манифестами: `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml` | Те же объекты, плюс labels Helm, checksum, preStop |
| 2 | `helm get manifest orders-api -n sandbox`, `helm get values orders-api -n sandbox` | Что Helm хранит про релиз (secret `sh.helm.release.v1.*` в namespace) |
| 3 | `./scripts/build-images.ps1 -Services orders-api -Tag 0.2.0`, поменять `tag` в `orders-api.yaml`, `./scripts/deploy-apps.ps1 -Services orders-api`; затем `helm rollback orders-api -n sandbox` | `/api/info` → `0.2.0`, после отката `0.1.0`; `helm history` |
| 4 | Поменять `Frontend__Greeting` в `frontend.yaml` и задеплоить | Поды frontend пересоздаются сами (checksum) |
| 5 | Поставить `image.tag: "9.9.9"` и задеплоить через `deploy-apps.ps1` | `--rollback-on-failure`: через таймаут Helm сам откатит релиз; в `helm history` — failed + rollback |
| 6 | В values написать `replcas: 3` или `image.tag: latest` | Схема отклонит до похода в кластер |
| 7 | `helm uninstall worker -n sandbox`, затем `./scripts/deploy-apps.ps1 -Services worker` | Все объекты релиза удалены и созданы заново |
| 8 | Посмотреть владельцев полей: `kubectl get deploy orders-api -n sandbox -o yaml --show-managed-fields`; затем `kubectl scale deploy/orders-api -n sandbox --replicas=5` и снова `helm upgrade` | `kubectl scale` становится владельцем `spec.replicas` → конфликт SSA при следующем upgrade. Ручные правки в кластере, управляемом Helm, — это drift |
| 9 | `shutdownDelaySeconds: 0`, задеплоить, затем `kubectl rollout restart` под опросом curl в цикле | Снова появятся потерянные запросы |

## Шпаргалка

```powershell
helm lint infra/helm/sandbox-service -f infra/helm/releases/sandbox/orders-api.yaml
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml
# diff против живого кластера (kubectl diff требует утилиту diff — она есть в Git for Windows):
$env:Path += ';C:\Program Files\Git\usr\bin'
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml --skip-tests | kubectl diff -n sandbox -f -
helm list -n sandbox
helm history orders-api -n sandbox
helm rollback orders-api <revision> -n sandbox --wait
helm test orders-api -n sandbox
```

| Симптом | Причина |
|---|---|
| `invalid ownership metadata` | Объект создан не этим релизом (kubectl или другой релиз) |
| `Apply failed with N conflicts: conflicts with "<manager>"` | SSA: полем владеет другой менеджер (kubectl, `kubectl scale`, другой контроллер) |
| `values don't meet the specifications of the schema` | Ошибка в values — читать путь в сообщении |
| `field is immutable` (selector) | Поменялись selectorLabels — Deployment придётся пересоздать |
| Релиз `failed`, а поды работают | Упал `--wait` по таймауту; смотреть `kubectl get events`, `helm history` |
| Объект «завис» после переименования в чарте | Сирота: Helm удаляет только то, что было в его прошлой ревизии |
