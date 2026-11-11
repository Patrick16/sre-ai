# 09. Окружение разработки (Фаза 0)

## Инструменты и зачем каждый

| Инструмент | Версия у нас | Что это | Зачем в проекте |
|---|---|---|---|
| **Docker Desktop** | 29.8 | Запускает контейнеры (на Windows — внутри WSL2) | Собирать образы сервисов; на нём же работают «ноды» kind |
| **.NET SDK** | 10.0.401 (зафиксировано в `global.json`) | Компилятор, `dotnet build/test/run` | Агент и sandbox-сервисы |
| **kind** | 0.33 | Kubernetes in Docker: каждая нода кластера — Docker-контейнер | Локальный мультинодовый кластер за минуту |
| **kubectl** | 1.33 (см. ниже) | CLI к kube-apiserver | Основной способ смотреть и менять кластер |
| **Helm** | 4.3 | Пакетный менеджер k8s | Устанавливать инфраструктуру и наши чарты (с Фазы 2–3) |
| **k9s** | 0.51 | TUI для кластера в терминале | Быстро смотреть поды, логи, события — удобнее, чем десятки команд kubectl |

Установка (уже сделана; на новой машине):

```powershell
winget install --id Kubernetes.kind -e
winget install --id Helm.Helm -e
winget install --id Derailed.k9s -e
```

### Нюанс: version skew kubectl

Kubernetes официально поддерживает kubectl в пределах **±1 минорной версии** от сервера.
Сейчас клиент 1.33 (поставлен вместе с Docker Desktop), а кластер 1.37. Базовые команды
работают, но новые поля и флаги могут вести себя неожиданно. Обновить:

```powershell
winget install --id Kubernetes.kubectl -e
```

После установки проверьте, какой kubectl идёт первым в `PATH` (`Get-Command kubectl`) —
версия Docker Desktop лежит в `C:\WINDOWS\system32` и может перекрывать новую.

## Контейнер, образ, реестр — за 30 секунд

- **Образ** (image) — неизменяемый «снимок» файловой системы с приложением и всем, что ему нужно.
- **Контейнер** — запущенный процесс из образа, изолированный от остальной системы.
- **Реестр** (registry) — хранилище образов (Docker Hub, GHCR). Kubernetes скачивает образы из реестра.
- В kind есть упрощение: образ можно загрузить прямо в ноды командой `kind load docker-image`,
  без реестра. В проде так не делают — там всегда реестр.

## Чем kind отличается от «настоящего» кластера

| | kind | Прод (EKS/AKS/GKE или bare metal) |
|---|---|---|
| Ноды | Docker-контейнеры на одной машине | Отдельные VM/серверы |
| Отказ ноды | `docker stop sre-ai-worker` | Реальный сбой железа/сети |
| LoadBalancer | Нет из коробки (нужен MetalLB/cloud-provider-kind) | Облачный балансировщик |
| Хранилище | `local-path` — папка на диске ноды | Сетевые диски, репликация |
| Control plane | Одна нода | Обычно 3 для отказоустойчивости |

Всё, что изучаем на уровне объектов k8s (Deployments, Services, Ingress, RBAC…), работает одинаково.
Отличается только «нижний» слой: сеть, диски, балансировщики.

## Что сейчас внутри кластера

После `scripts/cluster-up.ps1` (`kubectl get pods -A`):

| Под | Компонент из [03](03-kubernetes-concepts.md) | Роль |
|---|---|---|
| `kube-apiserver-*` | API-сервер | Единственная точка входа: kubectl, контроллеры, наш агент |
| `etcd-*` | etcd | База желаемого состояния |
| `kube-scheduler-*` | Scheduler | Выбирает ноду для каждого нового пода |
| `kube-controller-manager-*` | Controller manager | Reconcile-циклы: Deployment → ReplicaSet → Pods и т.д. |
| `coredns-*` (×2) | DNS кластера | Резолвит `orders-api.sandbox.svc.cluster.local` |
| `kube-proxy-*` (по одному на ноду) | kube-proxy | Сетевые правила, которые реализуют Services |
| `kindnet-*` (по одному на ноду) | CNI | Сеть между подами (поддерживает NetworkPolicy) |
| `local-path-provisioner-*` | Provisioner для StorageClass `standard` | Создаёт PV под PVC (Фаза 4) |

Заметьте: `kube-proxy` и `kindnet` — это **DaemonSet'ы** (по поду на каждой ноде), а
компоненты control plane — **static pods** (их запускает kubelet из файлов на ноде, а не API-сервер).

### Конфигурация кластера

[`infra/cluster/kind-config.yaml`](../infra/cluster/kind-config.yaml):
- 1 control-plane + 2 worker — чтобы были видны распределение подов по нодам и «падение ноды»;
- на control-plane label `ingress-ready=true` и проброс портов 80/443 с хоста — понадобится в Фазе 2 для Ingress
  (только на `127.0.0.1`, чтобы песочница не была видна из локальной сети);
- `containerdConfigPatches` — включает в containerd нод каталог зеркал реестров (см. ниже).

### Кеш образов (pull-through registry)

**Проблема.** Каждая нода kind — отдельный контейнер со своим containerd и своим хранилищем образов. Образы
Docker Desktop она не видит. Наши образы `build-images.ps1` кладёт в ноды сам (`kind load`), а образы сообщества
(Prometheus, Grafana, Loki, Alloy, postgres…) каждая нода качает из интернета сама — и снова после каждого
пересоздания кластера. Подключить хранилище Docker Desktop через volume нельзя: два демона containerd на одном
хранилище портят его метаданные, а папка Windows не поддерживает overlayfs.

**Решение** — [scripts/registry-cache.ps1](../scripts/registry-cache.ps1) (его вызывает `cluster-up.ps1`):

```
нода kind ──containerd──► /etc/containerd/certs.d/quay.io/hosts.toml: «сначала спроси кеш»
                              │
                              ▼
        kind-cache-quay-io (registry:2.8.3, режим proxy) ──► quay.io   (только при первом запросе)
                              │
                         volume kind-cache-quay-io  ← переживает cluster-down
```

- Три кеша — по одному на реестр: `docker.io`, `quay.io`, `registry.k8s.io` (один экземпляр registry в режиме
  proxy умеет только один upstream). Все в Docker-сети `kind`, ноды ходят к ним по имени контейнера.
- Если кеш недоступен, containerd молча идёт в интернет напрямую (`server = ...` в `hosts.toml`) — кеш ускоряет,
  но не является точкой отказа.
- Так же устроены зеркала в компаниях (Harbor, Artifactory, ECR pull-through cache): быстрее, не упираешься в
  лимиты Docker Hub, и кластер может стартовать, даже если внешний реестр лежит.

Измерено: образ `grafana/alloy` первый раз — 337 с (скорость интернета), с другой ноды уже из кеша — **8.6 с**.

**Адреса, которые зависят от вашего Docker.** В NetworkPolicy агента зашиты сеть kind (`172.18.0.0/16`) и
`host.docker.internal` (`192.168.65.254`). На другом Docker они другие, и тогда агент молча теряет API кластера или LLM.
`./scripts/check-network-assumptions.ps1` читает реальные значения (сеть `kind`, `host.docker.internal` изнутри ноды) и
сообщает, совпадают ли они с values.

**Почему `registry:2`, а не `registry:3`.** С `registry:3` (distribution v3) большие слои падали ровно через 30 с с
`500 … context canceled`: в режиме proxy v3 отдаёт слой клиенту только после того, как скачал его целиком, и
containerd не дожидается. Маленькие образы при этом проходили, и containerd молча уходил в интернет — то есть кеш
«как бы работал», но ничего не ускорял. v2 отдаёт слой по мере скачивания. Мораль: проверять не «настроено», а
«реально попадает в кеш» (`/v2/_catalog`, время скачивания).

```powershell
./scripts/registry-cache.ps1                  # поднять кеши и настроить ноды (идемпотентно)
docker exec kind-cache-docker-io wget -qO- http://localhost:5000/v2/_catalog   # что закешировано
./scripts/registry-cache.ps1 -Remove          # убрать контейнеры (данные остаются в volumes)
./scripts/registry-cache.ps1 -Remove -Purge   # убрать и данные
```

## .NET solution

Файл `SreAi.slnx` в корне (новый XML-формат solution в .NET 10, вместо `.sln`):

```
src/SreAgent.Core          classlib   ← от него зависят все
src/SreAgent.Tools         classlib   → Core
src/SreAgent.LlmClients    classlib   → Core
src/SreAgent.Host          web        → Tools, LlmClients
tests/SreAgent.Core.Tests  xunit      → Core
```

Проекты пока пустые — код появится в Фазе 7. Sandbox-сервисы добавятся в Фазе 1.

## Упражнения (руками)

**Снимок проекта на момент этой фазы** — тег `phase-0` (коммит `fae3a2c`). Упражнения писались под него; нужен,
если на текущем проекте что-то уже не воспроизводится. Повторным прогоном не проверялся — это состояние, на
котором упражнения были составлены. Отдельная папка (ваша рабочая копия не меняется):

```powershell
git worktree add ../sre-ai-phase-0 phase-0
cd ../sre-ai-phase-0
./scripts/cluster-down.ps1                   # имя кластера общее (sre-ai): сначала удалить текущий
./scripts/cluster-up.ps1
# ... упражнения ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-0 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # собрать текущий проект заново
```

**Предусловия и откат.** Упражнения этой фазы можно делать в любом порядке и раньше остальных фаз; нужен только
рабочий кластер. Если что-то сломали или хотите начать с чистого состояния (упражнение 6 делает то же самое):

```powershell
./scripts/reset-exercise.ps1               # вернуть приложения (после docker stop ноды и т.п.), данные сохраняются
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # с нуля, ~9 мин, данные теряются
```

1. `kubectl get nodes -o wide` — найти внутренние IP нод. Потом `docker ps` — убедиться, что «ноды» — это контейнеры.
2. `kubectl get pods -n kube-system -o wide` — на какой ноде живёт каждый компонент и почему control plane только на одной.
3. Открыть `k9s`, нажать `:pods`, выбрать `coredns`, нажать `l` (логи), `d` (describe). Выйти — `:q`.
4. `kubectl api-resources | Measure-Object` — сколько типов объектов знает кластер «из коробки» (потом сравнить после установки cert-manager/Prometheus — они добавят CRD).
5. `docker stop sre-ai-worker2`, подождать минуту, `kubectl get nodes` — нода `NotReady`. `docker start sre-ai-worker2` — вернулась. Это первый «хаос-эксперимент».
6. `scripts/cluster-down.ps1` и снова `scripts/cluster-up.ps1` — кластер пересоздаётся с нуля примерно за минуту. Не бойтесь ломать.
