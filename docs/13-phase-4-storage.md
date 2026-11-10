# 13. Фаза 4: хранение данных — PV, PVC, StatefulSet, PostgreSQL

Теория — разделы «StatefulSet» и «Persistent Volumes» в [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Зачем

В Фазе 1 заказы жили в памяти пода: у каждой из 3 реплик был свой список, а после рестарта всё пропадало.
Теперь orders-api (0.2.x) хранит заказы в PostgreSQL, а сама база хранит данные на постоянном томе.

## Почему свой чарт, а не Bitnami или оператор

| Вариант | Почему нет (или «потом») |
|---|---|
| Чарт Bitnami PostgreSQL | С осени 2025 бесплатные образы Bitnami перенесены в архивный `bitnamilegacy` без обновлений безопасности; актуальные — по платной подписке ([bitnami/charts#35164](https://github.com/bitnami/charts/issues/35164)). Тот же урок, что с ingress-nginx в Фазе 2: следить за жизненным циклом зависимостей |
| Оператор **CloudNativePG** | Так делают в проде: репликация, автоматический failover, бэкапы в S3, PITR — всё через CRD `Cluster`. Но он **не использует StatefulSet** (сам управляет подами и PVC), а нам нужно увидеть механику руками. Отличный «следующий уровень» — упражнение 9 |
| **Свой чарт** [infra/helm/postgres](../infra/helm/postgres/) на официальном образе `postgres:18.6-trixie` | ✅ Видно всё: StatefulSet, headless Service, `volumeClaimTemplates`, PVC, запуск без root |

## Что развёрнуто

```
orders-api ×3 ──► Service postgres:5432 ──► postgres-0 ──► PVC data-postgres-0 ──► PV pvc-… ──► папка на диске ноды sre-ai-worker
                  Service postgres-headless (clusterIP: None) → DNS postgres-0.postgres-headless.sandbox.svc.cluster.local
```

| Объект | Откуда | Зачем |
|---|---|---|
| StatefulSet `postgres` (1 реплика) | [statefulset.yaml](../infra/helm/postgres/templates/statefulset.yaml) | Стабильное имя пода `postgres-0` и «свой» том, который переживает пересоздание пода |
| `volumeClaimTemplates` → PVC `data-postgres-0` | там же | PVC создаётся сам при первом старте и навсегда закреплён за именем пода |
| `persistentVolumeClaimRetentionPolicy: Retain` | там же | PVC **не удаляется** при `helm uninstall` и при уменьшении реплик |
| Headless Service + обычный Service | [services.yaml](../infra/helm/postgres/templates/services.yaml) | Стабильное DNS-имя конкретного пода / адрес для клиентов |
| Secret `postgres-credentials` | генерирует [deploy-apps.ps1](../scripts/deploy-apps.ps1) | Пароль общий у базы и orders-api; **не перегенерируется**, иначе приложения не войдут в уже инициализированную базу |

### Запуск PostgreSQL без root
Официальный образ по умолчанию стартует от root и потом переключается на пользователя `postgres` — Pod Security
`restricted` такое не пропустит. Поэтому: `runAsUser/runAsGroup/fsGroup: 999` (uid пользователя `postgres` в образе),
корень ФС только для чтения, а записываемые места — отдельные тома: PVC в `/var/lib/postgresql`
(в PostgreSQL 18 данные лежат в `/var/lib/postgresql/18/docker`), `emptyDir` для `/var/run/postgresql` (сокет) и `/tmp`.

### Что изменилось в orders-api
- Заказы — таблица `orders`; поле `storedOnPod` стало `createdByPod` (теперь это «какой под принял заказ», а не «где он лежит»).
- Схема создаётся **лениво**, при первом запросе, под `pg_advisory_xact_lock` — три реплики стартуют одновременно
  и иначе устроили бы гонку `CREATE TABLE`. В проде миграции обычно выносят в отдельный Job (например, Helm-хук `pre-upgrade`).
- **Probes не зависят от базы.** Если база упала, поды orders-api не перезапускаются и не выпадают из балансировки,
  а отвечают `503 database unavailable`. Для людей, дашбордов и агента есть отдельный `/health/db`.

## Что обнаружили при проверке

### 1. Общее состояние
Оба пода frontend через **разные** поды orders-api видят одни и те же 5 заказов; worker обработал все (`processed: 5`).
Эффект «у каждой реплики свой список» из Фазы 1 исчез.

### 2. Удаление пода базы — данные на месте, простой ~3.5 с
`kubectl delete pod postgres-0` под непрерывными запросами к `/api/orders` (каждые 300 мс):
```
probe 200: 11   probe 503: 7      (503 с 36.4 по 39.9 с)
orders before: 5 → after: 5,  PVC uid тот же
LOG:  database system was shut down at ...   ← чистое завершение, без crash recovery
```
StatefulSet пересоздал под с **тем же именем** на **той же ноде** и подключил **тот же** PVC. Клиенты всё это время
получали аккуратный 503, а не зависание. PostgreSQL успел корректно завершиться (SIGINT из `STOPSIGNAL` образа →
«fast shutdown»; `terminationGracePeriodSeconds: 60` дал на это время).

### 3. Данные — это просто папка на одной ноде
```
PV path = /var/local-path-provisioner/pvc-…_sandbox_data-postgres-0
nodeAffinity: kubernetes.io/hostname in [sre-ai-worker]
drwxrwxrwx root  .        ← провижнер создаёт каталог 0777
drwxr-xr-x 999   18/      ← дальше всё принадлежит пользователю postgres (63 МБ после initdb)
```
`nodeAffinity` у PV — главное ограничение локального хранилища: если нода `sre-ai-worker` умрёт, под базы **не
сможет переехать** на другую ноду — его том физически там. Поэтому в проде используют сетевые диски (EBS, Azure Disk,
Ceph) или репликацию на уровне самой базы (CloudNativePG, Patroni).

### 4. `helm uninstall` не удалил данные
StatefulSet и под удалены, PVC остался (политика `Retain`); `/api/orders` → 503. После повторной установки — все 5 заказов.

### 5. Удаление PVC — настоящая потеря данных
- Пока под использует PVC, удаление «зависает»: finalizer `kubernetes.io/pvc-protection` держит его в `Terminating`,
  а данные продолжают читаться.
- После удаления пода StatefulSet создал под и **новый пустой** PVC; старый PV удалён вместе с папкой
  (у StorageClass `standard` — `reclaimPolicy: Delete`).

### 6. Найденный баг: health check зелёный, а сервис сломан
Первая версия orders-api (0.2.0) запоминала «схема уже создана». После подмены базы на пустую:
```
/health/db: Healthy
/api/orders: 503 503 503 503 …    ← навсегда
log: Database unavailable: 42P01: relation "orders" does not exist
```
База жива, health check зелёный, а сервис не работает — и текст ошибки ещё и врёт («database unavailable»).
Исправлено в 0.2.1: при `UndefinedTable` флаг сбрасывается, схема пересоздаётся, запрос повторяется.
Отличный будущий сценарий для агента: «проверки зелёные, пользователи страдают».

### 7. Первый запрос после рестарта базы падает
Даже с 0.2.1 первый запрос на каждом поде после перезапуска базы — 503:
`57P01: terminating connection due to administrator command`. Соединения в пуле Npgsql «умерли» вместе с сервером,
и первое использование каждого из них падает. Npgsql сам не повторяет запросы — это делают на уровне приложения
(retry для идемпотентных операций, например `Microsoft.Extensions.Resilience`). Упражнение 8.

### 8. Найдено при код-ревью 2026-11-10 (orders-api 0.5.0)
- **Ошибка клиента выглядела как падение БД.** `POST /orders` без `item` или с `null` нарушал `NOT NULL`, а
  `PostgresException` — это `NpgsqlException`, поэтому фильтр отвечал 503 «database unavailable»: SLO горел, а агент
  искал несуществующую проблему с базой. Теперь: валидация тела (непустой `item`, до 200 символов) → 400; ошибки БД
  классов 22/23 (плохие данные / нарушенное ограничение) тоже 400; 503 — только когда база правда недоступна.
- **Список «замерзал».** `GET /orders` отдавал первые 100 заказов за всё время (`ORDER BY created_at`), поэтому страница
  frontend всегда показывала одно и то же. Теперь общий список — новейшие первыми (`DESC`); очередь воркера
  (`status=pending`) осталась по возрасту — это FIFO, так и нужно. Добавлен индекс по `created_at`.
- **База росла бесконечно.** loadgen создаёт ≈1 заказ/с, а PVC всего 1 Гб: через недели диск кончился бы и PostgreSQL
  перестал бы принимать запись. Теперь `Orders__RetentionHours: "24"` — фоновая задача удаляет *обработанные* заказы
  старше суток (необработанные не трогает). Значит, в упражнениях о сохранности данных заказы старше суток уже
  не найдутся — это ожидаемо.

## Упражнения

| # | Сделать | Что ожидать |
|---|---|---|
| 1 | `kubectl get sts,pod,pvc,pv -n sandbox` и `kubectl describe pvc data-postgres-0 -n sandbox` | Цепочка StatefulSet → под → PVC → PV, событие `ProvisioningSucceeded` |
| 2 | `kubectl exec -n sandbox postgres-0 -- psql -U orders -c "select status, count(*) from orders group by 1"` | Данные прямо в базе |
| 3 | `nslookup` до `postgres` и `postgres-0.postgres-headless` из временного пода (образ `busybox`, нужен securityContext под `restricted`) | Обычный Service — ClusterIP; headless — сразу IP пода |
| 4 | `docker exec sre-ai-worker ls /var/local-path-provisioner` (имя ноды — из `kubectl get pod postgres-0 -o wide`) | Папки всех PV на этой ноде |
| 5 | `docker stop <нода с postgres-0>` и подождать ~6 минут | Под зависнет в `Terminating`/`Unknown`, и StatefulSet **не создаст замену**: он гарантирует «не больше одного `postgres-0`», а узнать, жив ли старый, не может. После `kubectl delete pod postgres-0 --force` новый под будет `Pending` с `volume node affinity conflict` — том физически на мёртвой ноде. Вернуть: `docker start` |
| 6 | Скопировать данные: `kubectl exec -n sandbox postgres-0 -- pg_dump -U orders orders > .local/orders.sql` | Простейший бэкап; затем удалить PVC и восстановить через `psql` |
| 7 | `kubectl patch pvc data-postgres-0 -n sandbox -p '{"spec":{"resources":{"requests":{"storage":"2Gi"}}}}'` | Отказ: у `standard` нет `allowVolumeExpansion` — размер тома тоже надо планировать |
| 8 | Добавить в orders-api повтор для `NpgsqlException { IsTransient: true }` на чтениях и повторить перезапуск базы | Первые запросы после рестарта больше не падают |
| 9 | (Продвинуто) Поставить оператор CloudNativePG и `Cluster` с 3 инстансами; удалить под primary | Автоматический failover на реплику — то, чего у одиночного StatefulSet нет |

## Шпаргалка отладки

| Симптом | Где смотреть | Частая причина |
|---|---|---|
| PVC в `Pending` | `kubectl describe pvc` | Нет StorageClass / провижнера; у `WaitForFirstConsumer` PVC ждёт, пока под запланируют |
| Под `Pending`: `volume node affinity conflict` | `kubectl describe pod` | Нода с локальным PV недоступна |
| PVC «завис» в `Terminating` | `kubectl get pvc -o yaml` → finalizers | Его ещё использует под (`pvc-protection`) |
| postgres: `could not create directory … Permission denied` | `kubectl logs postgres-0` | Неверный uid/fsGroup или том смонтирован не туда |
| `password authentication failed` | логи postgres | Пароль в Secret изменили после initdb — база помнит старый |
| Приложение 503, а `/health/db` зелёный | логи приложения (`42P01`) | Схема не создана / база подменена |
| Первые запросы после рестарта базы падают | логи приложения (`57P01`) | Мёртвые соединения в пуле, нет retry |
