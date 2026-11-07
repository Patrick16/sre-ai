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
- на control-plane label `ingress-ready=true` и проброс портов 80/443 с хоста — понадобится в Фазе 2 для Ingress.

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

1. `kubectl get nodes -o wide` — найти внутренние IP нод. Потом `docker ps` — убедиться, что «ноды» — это контейнеры.
2. `kubectl get pods -n kube-system -o wide` — на какой ноде живёт каждый компонент и почему control plane только на одной.
3. Открыть `k9s`, нажать `:pods`, выбрать `coredns`, нажать `l` (логи), `d` (describe). Выйти — `:q`.
4. `kubectl api-resources | Measure-Object` — сколько типов объектов знает кластер «из коробки» (потом сравнить после установки cert-manager/Prometheus — они добавят CRD).
5. `docker stop sre-ai-worker2`, подождать минуту, `kubectl get nodes` — нода `NotReady`. `docker start sre-ai-worker2` — вернулась. Это первый «хаос-эксперимент».
6. `scripts/cluster-down.ps1` и снова `scripts/cluster-up.ps1` — кластер пересоздаётся с нуля примерно за минуту. Не бойтесь ломать.
