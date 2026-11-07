# 01. Роадмап

Проект идёт фазами. Каждая фаза — это **учебная цель** (что понять) и **результат**
(что должно заработать). Не переходим к следующей фазе, пока не можем объяснить
предыдущую своими словами. Отмечайте чек-боксы по мере прохождения.

Принцип: сначала делаем вручную (`kubectl apply` сырых манифестов), чтобы понять
механику, и только потом автоматизируем (Helm, скрипты).

---

## Фаза 0. Окружение и структура

**Понять:** что такое контейнер, образ, реестр; чем kind отличается от «настоящего» кластера.

- [x] Структура репозитория и документация
- [ ] Установить: Docker Desktop, .NET SDK, kubectl, kind, Helm, k9s (TUI для кластера)
- [ ] Создать .NET solution (`src/SreAi.sln`) с пустыми проектами
- [ ] Скрипт `scripts/cluster-up` / `cluster-down` (kind, 1 control-plane + 2 worker)
- [ ] Заполнить раздел Commands в `CLAUDE.md`

## Фаза 1. Базовый Kubernetes

**Понять:** Pod, ReplicaSet, Deployment, Service, Namespace, ConfigMap, Secret, probes, requests/limits.

- [ ] 3 sandbox-сервиса на .NET: `frontend` → `orders-api` → `worker` (минимальный HTTP)
- [ ] Dockerfile для каждого, загрузка образов в kind (`kind load docker-image`)
- [ ] Сырые манифесты в `infra/manifests/phase-1/`: Namespace, Deployment, Service
- [ ] ConfigMap с настройками, Secret с «паролем» — подключить как env и как файл
- [ ] liveness/readiness/startup probes, resources requests/limits
- [ ] Эксперимент: удалить под руками и посмотреть, как Deployment его пересоздаёт

## Фаза 2. Трафик снаружи: Ingress и TLS

**Понять:** почему Service недостаточно, что делает Ingress Controller, как работает TLS.

- [ ] Установить ingress-nginx
- [ ] Ingress для `frontend` по хосту `sandbox.local`
- [ ] cert-manager + self-signed ClusterIssuer, HTTPS на Ingress
- [ ] Эксперимент: path-based и host-based маршрутизация

## Фаза 3. Helm

**Понять:** chart, release, values, шаблоны, зачем пакетировать манифесты.

- [ ] Общий chart `infra/helm/sandbox-service` (один шаблон для всех сервисов)
- [ ] Umbrella chart / values-файлы для окружения
- [ ] Перевести всё из фазы 1–2 на Helm, удалить сырые манифесты из деплоя
- [ ] Эксперимент: `helm upgrade` с новой версией, `helm rollback`

## Фаза 4. Хранение данных

**Понять:** PV, PVC, StorageClass, StatefulSet, почему БД — не Deployment.

- [ ] PostgreSQL как StatefulSet (через Helm-чарт) с PVC
- [ ] `orders-api` пишет в БД
- [ ] Эксперимент: удалить под БД — данные сохранились; удалить PVC — пропали

## Фаза 5. Observability

**Понять:** метрики vs логи vs трейсы, pull-модель Prometheus, PromQL, SLI/SLO, алерты.

- [ ] kube-prometheus-stack (Prometheus, Alertmanager, Grafana, node-exporter, kube-state-metrics)
- [ ] Метрики из .NET-сервисов (OpenTelemetry / prometheus-net) + ServiceMonitor
- [ ] Loki + сборщик логов, структурные логи из сервисов
- [ ] Дашборд «Golden signals» (latency, traffic, errors, saturation) для каждого сервиса
- [ ] SLO для `orders-api` (например, 99% запросов < 300 мс) и алерты по burn rate
- [ ] Alertmanager → webhook (пока заглушка, позже — агент)

## Фаза 6. Безопасность сети: NetworkPolicy, mTLS, Service Mesh

**Понять:** zero-trust внутри кластера, sidecar-прокси, что именно шифрует mTLS.

- [ ] NetworkPolicy: по умолчанию deny, разрешить только нужные связи
- [ ] Установить Linkerd, включить inject для namespace sandbox
- [ ] Проверить, что трафик идёт по mTLS (`linkerd viz edges`)
- [ ] Retries/timeouts на уровне mesh, метрики mesh в Grafana
- [ ] (Опционально) сравнить с Istio — понять, чем отличаются

## Фаза 7. Агент MVP (read-only)

**Понять:** как устроен agent harness, tool calling, контекст LLM, Ralph loop и RALF-итерация.

- [ ] Абстракция LLM-клиента: OpenAI-совместимый API и LM Studio через один интерфейс
- [ ] Read-only tools: состояние подов/деплойментов, события k8s, PromQL-запрос, запрос логов из Loki, активные алерты
- [ ] RALF-итерация v1: Retrieve → Analyze → (действий нет) → Feedback (отчёт)
- [ ] Ralph loop v1: TaskSpec, RunState на диске, completion check, лимиты, backoff — в режиме наблюдения
- [ ] Запуск по расписанию и по вебхуку Alertmanager
- [ ] Отчёт в `reports/` в Markdown
- [ ] Метрики и логи самого агента

## Фаза 8. Безопасные действия и guardrails

**Понять:** как дать агенту права, не дав ему сломать кластер.

- [ ] Отдельный ServiceAccount + RBAC Role только с нужными verbs
- [ ] Allowlist действий (restart, scale в пределах, rollback) + policy-проверки
- [ ] Режимы: `dry-run` (по умолчанию) → `approve` (подтверждение человеком) → `auto`
- [ ] Audit log каждого решения и действия
- [ ] Лимиты: не больше N действий за окно, circuit breaker, cooldown
- [ ] Проверка эффекта: после действия агент перепроверяет, стало ли лучше
- [ ] Ralph loop с действиями: «чинить, пока не починено», запрет повтора бесполезного действия, эскалация при degraded/нет прогресса

## Фаза 9. Chaos engineering

**Понять:** steady state, гипотеза, blast radius, game day.

- [ ] Chaos Mesh: pod-kill, network-delay, CPU stress
- [ ] Свой простой chaos-инструмент на .NET (например, «убить случайный под в namespace»)
- [ ] «Плохие» режимы в sandbox-сервисах (утечка памяти, медленные ответы, 500-ки по флагу)
- [ ] Сценарии: эксперимент → алерт → агент → действие → отчёт; оценить качество агента
- [ ] Game day: серия экспериментов с разбором

## Фаза 10. Полировка

- [ ] Один скрипт поднимает всё с нуля
- [ ] GitOps (Argo CD) — опционально, как «следующий уровень»
- [ ] Финальная документация: что выучено, что бы сделал иначе
