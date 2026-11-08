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

- [x] kube-prometheus-stack (Prometheus на PVC, Alertmanager, Grafana по HTTPS, node-exporter, kube-state-metrics) — см. [14](14-phase-5-observability.md)
- [x] Метрики из .NET-сервисов (OpenTelemetry, отдельный порт 9090) + PodMonitor; postgres-exporter + ServiceMonitor; метрики Traefik и cert-manager
- [x] Loki + Grafana Alloy (DaemonSet), структурные JSON-логи из сервисов
- [x] Дашборд «Golden signals» (latency, traffic, errors, saturation + burn rate, зависимости, логи)
- [x] SLO для `orders-api` (99% < 250 мс — граница bucket'а) и `frontend` (99% < 500 мс), multi-window burn-rate алерты; `PostgresDown`
- [x] Alertmanager → webhook заглушки агента (`SreAgent.Host`); сквозной сценарий «база упала → алерт → агент» проверен
- [x] Фоновая нагрузка (k6, ~5 rps через Traefik)
- [ ] Упражнения из [14-phase-5-observability.md](14-phase-5-observability.md): silence, SLO по латентности, LogQL
- [ ] (Опционально) трейсы: OpenTelemetry → Tempo

## Фаза 6. Безопасность сети: NetworkPolicy, mTLS, Service Mesh

**Понять:** zero-trust внутри кластера, sidecar-прокси, что именно шифрует mTLS.

- [x] NetworkPolicy: default-deny + DNS на namespace, разрешения объявляет каждый чарт; блокировка проверена — см. [15](15-phase-6-network-security.md)
- [x] Свой ServiceAccount на сервис (identity для mTLS), без токена API
- [x] Linkerd edge-26.11.1: identity через cert-manager, linkerd-cni (совместимость с Pod Security `restricted`), native sidecar, inject для `sandbox` и `sre-agent`
- [x] mTLS проверен по метрикам прокси (`tls=true`, `client_id` = ServiceAccount); балансировка по запросам исправила эффект Фазы 1
- [x] Retries (только GET) и timeouts через Gateway API HTTPRoute; эффект измерен; метрики прокси в нашем Prometheus, Linkerd Viz поверх него
- [ ] Упражнения из [15-phase-6-network-security.md](15-phase-6-network-security.md): mesh для Traefik, `AuthorizationPolicy` по identity
- [ ] (Опционально) сравнить с Istio ambient — понять, чем отличаются

## Фаза 7. Агент MVP (read-only)

**Понять:** как устроен agent harness, tool calling, контекст LLM, Ralph loop и RALF-итерация.

- [x] Microsoft Agent Framework (преемник Semantic Kernel) поверх Microsoft.Extensions.AI — см. [16](16-phase-7-agent-mvp.md)
- [x] Абстракция LLM-клиента: OpenAI-совместимый API и LM Studio через один интерфейс; модель по умолчанию `qwen/qwen3.5-9b`, выбор из `AllowedModels`
- [x] Read-only tools: поды, workload'ы, события k8s, PromQL, логи из Loki, активные алерты
- [x] RALF-итерация v1: Retrieve (код) → Analyze (агент, свежая сессия, бюджет шагов) → Lead (observe-only) → Feedback (completion check)
- [x] Ralph loop v1: TaskSpec, RunState на диске, completion check «steady state namespace», лимиты, backoff
- [x] Запуск по вебхуку Alertmanager, по расписанию и вручную (`POST /runs`); один прогон на namespace
- [x] Отчёт в Markdown и audit log (JSONL) на PVC
- [x] Метрики и логи самого агента (+ `gen_ai.*` от Microsoft.Extensions.AI)
- [x] 17 тестов harness на «скриптованной» модели; RBAC только на чтение в `sandbox`; default-deny egress
- [x] Сквозной сценарий «база упала → агент → steady state» — `Resolved`; найдены и исправлены критерий steady state и устойчивость к сбоям модели
- [x] Финальный синтез отчёта по всем итерациям + «установленные факты» в RunState (итоговая причина бывала хуже промежуточной)
- [ ] Упражнения из [16-phase-7-agent-mvp.md](16-phase-7-agent-mvp.md): сравнение моделей, prompt injection

## Фаза 8. Безопасные действия и guardrails

**Понять:** как дать агенту права, не дав ему сломать кластер.

- [x] Действия `restart_deployment` (Auto) и `delete_pod` (Approve); модель только предлагает их полем в JSON — см. [17](17-phase-8-actions-guardrails.md)
- [x] Guardrails в коде: allowlist, проверка цели, kill switch (ConfigMap), лимиты за прогон и за окно, cooldown, circuit breaker, no-repeat, preconditions
- [x] Режимы `DryRun` (по умолчанию; `dryRun=All` в API) → `Approve` (`POST /approvals/{id}`, таймаут = «нет») → `Auto`
- [x] RBAC (patch deployments, delete pods, get одного ConfigMap) + ValidatingAdmissionPolicy (только аннотация рестарта, только поды Deployment); проверено `--as`
- [x] Audit каждого шага до и после; раздел «Действия» в отчёте строится из записи, не из текста модели
- [x] Проверка эффекта (1m 5xx + неготовые поды, до/после) → Improved / Unchanged / Degraded; Degraded → `Escalated`
- [x] Ralph loop с действиями: RunState помнит действия, повтор бесполезного запрещён; current-symptom (по итогам e2e); 53 теста harness
- [x] Сквозной сценарий в Auto: без действий, пока база лежит; после — рестарт orders-api; найдены «устаревшие данные» и «post hoc» в оценке эффекта
- [ ] Упражнения из [17](17-phase-8-actions-guardrails.md): kill switch, protected, prompt injection, ухудшающее действие

## Фаза 9. Chaos engineering

**Понять:** steady state, гипотеза, blast radius, game day.

- [x] Chaos Mesh 2.8.7 (только namespace `sandbox`): pod-kill, network-delay, CPU stress — гипотезы проверены, одна опровергнута — см. [18](18-phase-9-chaos-engineering.md)
- [x] Свой chaos-инструмент на .NET: `src/Chaos.Monkey` (случайный под, только `sandbox`, не последние ready, dry run по умолчанию)
- [x] «Плохие» режимы в sandbox-сервисах: задержка, 500-ки, утечка памяти — `faults:` в values, без рестарта подов
- [x] Агент 0.5.0: инцидент, объявленный человеком (`POST /runs {"incident": true}`); steady state включает ready-поды и отсутствие рестартов
- [x] Сценарии и оценка агента: `scripts/game-day.ps1` + `infra/chaos/scenarios.json` → scorecard
- [x] Game day: 5 сценариев с разбором; провальный (задержка БД) — повторён 3 раза: 0/3 → 2/3 за счёт данных Retrieve (латентность, возраст событий)
- [x] Находка: холодный старт mesh после перезапуска Docker Desktop → `scripts/recover-mesh.ps1`
- [ ] Упражнения из [18](18-phase-9-chaos-engineering.md)

## Фаза 10. Полировка

- [x] Один скрипт поднимает всё с нуля: `scripts/up.ps1` + `scripts/smoke-test.ps1` (24 проверки); проверено `cluster-down` → `up.ps1` — 8.5 мин — см. [19](19-phase-10-polish-and-lessons.md)
- [x] GitOps (Argo CD) — описан (как лёг бы на релизы, где столкнётся с действиями агента и хаосом), не внедрён
- [x] Финальная документация: что выучено, что бы сделал иначе, куда дальше
