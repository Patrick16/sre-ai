# sre-ai

Учебный sandbox-проект: Kubernetes-кластер «как на проде» + агент на .NET, который
следит за состоянием системы (метрики, логи, алерты), выполняет **безопасные**
действия и готовит отчёты.

Главная цель проекта — не результат, а понимание: разобраться на практике в SRE
и Kubernetes, и спроектировать/реализовать собственный agent harness. Агент работает
по **Ralph loop**: крутится, пока задача не решена. Каждая итерация — **RALF**:
Retrieve → Analyze → Lead → Feedback, см. [docs/05-agent-architecture.md](docs/05-agent-architecture.md).

## Структура репозитория

| Папка | Что внутри |
|---|---|
| [`docs/`](docs/00-overview.md) | Документация: обзор, роадмап, объяснение концепций k8s/SRE, дизайн агента, guardrails, хаос-инжениринг, отчётность, глоссарий |
| `infra/` | Всё для кластера: bootstrap кластера (`cluster/`), Helm-чарты (`helm/`), «сырые» k8s-манифесты (`manifests/`), конфигурация хаос-экспериментов (`chaos/`) |
| `src/` | .NET-решение: ядро агента, инструменты (tools), клиенты LLM, песочничные сервисы |
| `scripts/` | Скрипты bootstrap: поднять кластер, установить чарты, сгенерировать нагрузку/сбои |
| `reports/` | Отчёты, которые генерирует агент по итогам своих циклов/инцидентов |

## С чего начать

1. [docs/00-overview.md](docs/00-overview.md) — что это за проект и зачем он нужен.
2. [docs/01-roadmap.md](docs/01-roadmap.md) — пошаговый план по фазам: от пустого
   окружения до хаос-инжениринга и mTLS/service mesh. Все остальные документы
   привязаны к фазам из роадмапа.
3. [docs/glossary.md](docs/glossary.md) — если встретился незнакомый термин.

## Технологии (план)

- **Kubernetes**: локальный кластер (kind/minikube), Deployments, Services,
  Ingress, ConfigMaps, Secrets, PV/PVC, Helm, NetworkPolicy, mTLS, Service Mesh.
- **Observability**: Prometheus, Grafana, Alertmanager, Loki (или аналог для логов).
- **Agent**: .NET (C#), подключение к LLM через OpenAI-совместимый API или
  локально через LM Studio.
- **Chaos engineering**: Chaos Mesh и/или собственные простые инструменты на .NET.
- **Sandbox-сервисы**: минимальные .NET-приложения, имитирующие "прод"-топологию
  (frontend → backend → worker + БД), намеренно хрупкие для экспериментов.

## Статус

**Пройдены Фазы 0–2**: инструменты, .NET solution, kind-кластер
([docs/09](docs/09-dev-environment.md)), три sandbox-сервиса на сырых манифестах
([docs/10](docs/10-phase-1-kubernetes-basics.md)), Traefik + cert-manager и HTTPS на
`https://sandbox.localhost` ([docs/11](docs/11-phase-2-ingress-tls.md)). Дальше — Фаза 3 (Helm).
Код, манифесты и чарты появляются по мере прохождения фаз из [роадмапа](docs/01-roadmap.md).

```powershell
./scripts/cluster-up.ps1        # поднять кластер
./scripts/build-images.ps1      # собрать образы и загрузить в kind
./scripts/deploy-phase1.ps1     # развернуть sandbox
./scripts/deploy-phase2.ps1     # Traefik, cert-manager, HTTPS
curl.exe --ssl-no-revoke --cacert .local/sandbox-root-ca.crt https://sandbox.localhost/
``` Актуальный статус по
фазам отмечается чек-боксами прямо в роадмапе.
