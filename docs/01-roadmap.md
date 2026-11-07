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
- [x] Установить: Docker Desktop, .NET SDK, kubectl, kind, Helm, k9s (TUI для кластера) — см. [09](09-dev-environment.md)
- [x] Создать .NET solution (`SreAi.slnx` в корне) с пустыми проектами
- [x] Скрипт `scripts/cluster-up` / `cluster-down` (kind, 1 control-plane + 2 worker)
- [x] Заполнить раздел Commands в `CLAUDE.md`
- [ ] Обновить kubectl до версии кластера (сейчас 1.33 против 1.37)
- [ ] Пройти упражнения из [09-dev-environment.md](09-dev-environment.md)

## Фаза 1. Базовый Kubernetes

**Понять:** Pod, ReplicaSet, Deployment, Service, Namespace, ConfigMap, Secret, probes, requests/limits.

- [x] 3 sandbox-сервиса на .NET: `frontend` → `orders-api` ← `worker` (минимальный HTTP) — см. [10](10-phase-1-kubernetes-basics.md)
- [x] Общий multi-stage Dockerfile, загрузка образов в kind (`scripts/build-images.ps1`)
- [x] Сырые манифесты (`infra/manifests/phase-1/`, с Фазы 3 — в истории git): Namespace (Pod Security `restricted`), Deployment, Service
- [x] ConfigMap с настройками, Secret с API-ключом — подключены и как env, и как файл
- [x] liveness/readiness/startup probes, resources requests/limits, securityContext
- [ ] Пройти упражнения из [10-phase-1-kubernetes-basics.md](10-phase-1-kubernetes-basics.md) (удаление пода, scale, OOMKilled, rollout undo…)

## Фаза 2. Трафик снаружи: Ingress и TLS

**Понять:** почему Service недостаточно, что делает Ingress Controller, как работает TLS.

- [x] Установить Ingress Controller — Traefik (ingress-nginx закрыт в марте 2026), см. [11](11-phase-2-ingress-tls.md)
- [x] Ingress для `frontend` по хосту `sandbox.localhost`, `/api` → orders-api (path-based)
- [x] cert-manager + собственный CA (self-signed root → ClusterIssuer `sandbox-ca`), HTTPS и редирект с HTTP
- [ ] Упражнения из [11-phase-2-ingress-tls.md](11-phase-2-ingress-tls.md): host-based маршрут, ротация сертификата
- [ ] (Опционально) тот же маршрут через Gateway API (`Gateway` + `HTTPRoute`) на Traefik

## Фаза 3. Helm

**Понять:** chart, release, values, шаблоны, зачем пакетировать манифесты.

- [x] Общий chart `infra/helm/sandbox-service` (один шаблон для всех сервисов, schema, `helm test`) — см. [12](12-phase-3-helm.md)
- [x] Отдельный релиз и values-файл на сервис (`infra/helm/releases/sandbox/`) — выбран вместо umbrella-чарта
- [x] Перевести всё из фазы 1–2 на Helm без простоя (`--take-ownership`, чистка `managedFields`), удалить сырые манифесты
- [x] Эксперимент: `helm upgrade` с новой версией, `helm rollback` — 128/128 запросов без ошибок
- [x] Разделить деплой на платформу (`deploy-infra.ps1`) и приложения (`deploy-apps.ps1`); проверено с нуля
- [ ] Упражнения из [12-phase-3-helm.md](12-phase-3-helm.md) (failed release + авто-откат, drift через `kubectl scale`)

## Фаза 4. Хранение данных

**Понять:** PV, PVC, StorageClass, StatefulSet, почему БД — не Deployment.

- [x] PostgreSQL 18 как StatefulSet — свой чарт `infra/helm/postgres` (Bitnami больше не обновляется), PVC через `volumeClaimTemplates`, без root — см. [13](13-phase-4-storage.md)
- [x] `orders-api` 0.2.x пишет в БД; общее состояние у всех реплик, worker обрабатывает все заказы
- [x] Эксперимент: удалить под БД — данные сохранились (простой ~3.5 с, 503); `helm uninstall` — PVC сохранён (`Retain`); удалить PVC — данные пропали
- [x] Найден и исправлен баг «health зелёный, сервис сломан» после подмены базы (0.2.1)
- [ ] Упражнения из [13-phase-4-storage.md](13-phase-4-storage.md): падение ноды с PV, бэкап `pg_dump`, retry на мёртвых соединениях
- [ ] (Опционально) CloudNativePG: 3 инстанса и автоматический failover

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
