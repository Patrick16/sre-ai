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
| `scripts/` | Подъём (`up.ps1`), проверка (`smoke-test.ps1`), деплой, game day, восстановление mesh |

## С чего начать

1. [docs/00-overview.md](docs/00-overview.md) — что это за проект и зачем он нужен.
2. [docs/01-roadmap.md](docs/01-roadmap.md) — пошаговый план по фазам: от пустого
   окружения до хаос-инжениринга и mTLS/service mesh. Все остальные документы
   привязаны к фазам из роадмапа.
3. [docs/glossary.md](docs/glossary.md) — если встретился незнакомый термин.

## Технологии

- **Kubernetes**: kind (3 ноды), Deployments, StatefulSet, Services, Ingress (Traefik), ConfigMaps, Secrets,
  PV/PVC, Helm 4, Pod Security, NetworkPolicy default-deny, mTLS и service mesh (Linkerd), ValidatingAdmissionPolicy.
- **Observability**: Prometheus, Grafana, Alertmanager (kube-prometheus-stack), Loki + Alloy, OpenTelemetry
  в .NET-сервисах, SLO с multi-window burn-rate алертами.
- **Agent**: .NET 10 на Microsoft Agent Framework, локальная модель `qwen/qwen3.5-9b` в LM Studio (или любой
  OpenAI-совместимый API). Ralph loop + RALF, факты и синтез, два действия под guardrails.
- **Chaos engineering**: Chaos Mesh, свой `Chaos.Monkey` на .NET, «плохие режимы» в сервисах, game day со scorecard.
- **Sandbox-сервисы**: frontend → orders-api → PostgreSQL, worker; намеренно простые.

## Быстрый старт

Нужны Docker Desktop, kind, kubectl, Helm 4, .NET SDK 10, PowerShell 7 ([docs/09](docs/09-dev-environment.md)).

```powershell
./scripts/up.ps1           # всё с нуля одной командой (~20-30 мин с прогретыми кэшами), в конце — smoke test
./scripts/smoke-test.ps1   # проверить, что всё работает (в любой момент)
./scripts/recover-mesh.ps1 # после перезапуска Docker Desktop или сна ноутбука, если smoke test ругается на mesh или сертификаты
./scripts/game-day.ps1     # хаос-сценарии + оценка агента
./scripts/cluster-down.ps1 # удалить кластер (и все данные в нём)
```

| UI | Адрес |
|---|---|
| Приложение | https://sandbox.localhost |
| Grafana | https://grafana.localhost (`admin`, пароль: `./scripts/grafana-password.ps1`) |
| Prometheus | https://prometheus.localhost |
| Alertmanager | https://alertmanager.localhost |
| Linkerd Viz | `kubectl port-forward -n linkerd-viz svc/web 8084:8084` → http://localhost:8084 |
| SRE-агент | `kubectl port-forward -n sre-agent svc/sre-agent 8099:80` → `POST /runs`, `GET /runs/{id}/report`, `GET /actions`, `/approvals` |

Браузер не знает наш CA: либо `curl.exe --ssl-no-revoke --cacert .local/sandbox-root-ca.crt ...`, либо импортируйте
`.local/sandbox-root-ca.crt` в доверенные сами — это ваше решение.

## Статус

**Все 10 фаз пройдены** (упражнения в конце каждого документа фазы — для самостоятельной работы). Карта по фазам:

| Фаза | Документ |
|---|---|
| 0–1. Окружение, базовый Kubernetes | [09](docs/09-dev-environment.md), [10](docs/10-phase-1-kubernetes-basics.md) |
| 2. Ingress и TLS | [11](docs/11-phase-2-ingress-tls.md) |
| 3. Helm | [12](docs/12-phase-3-helm.md) |
| 4. Хранение данных | [13](docs/13-phase-4-storage.md) |
| 5. Observability | [14](docs/14-phase-5-observability.md) |
| 6. NetworkPolicy, mTLS, mesh | [15](docs/15-phase-6-network-security.md) |
| 7. Агент MVP | [16](docs/16-phase-7-agent-mvp.md) |
| 8. Безопасные действия | [17](docs/17-phase-8-actions-guardrails.md) |
| 9. Chaos engineering | [18](docs/18-phase-9-chaos-engineering.md) |
| 10. Всё с нуля, GitOps, итоги | [19](docs/19-phase-10-polish-and-lessons.md) |
