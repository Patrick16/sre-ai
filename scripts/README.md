# scripts/

Скрипты для работы с окружением. Пишутся на PowerShell (основная ОС — Windows),
по возможности кроссплатформенно (`pwsh`).

| Скрипт (план) | Что делает | Фаза |
|---|---|---|
| `cluster-up.ps1` / `cluster-down.ps1` ✅ | Создать / удалить kind-кластер `sre-ai` | 0 |
| `build-images.ps1` ✅ | Собрать Docker-образы сервисов и загрузить в kind; тег берётся из `infra/helm/releases/sandbox/<service>.yaml` (`-Tag` переопределяет) | 1 |
| `deploy-infra.ps1` ✅ | Платформа: namespace, Traefik, cert-manager, CA-issuers; экспорт корневого CA в `.local/` | 2–3 |
| `deploy-apps.ps1` ✅ | Secrets (один раз) + Helm-релизы: postgres → orders-api → worker → frontend (`-Services`), `--rollback-on-failure` | 3–4 |
| `port-forward.ps1` | Пробросить Grafana/Prometheus/Loki/Alertmanager на localhost для агента при локальном запуске | 5, 7 |
| `load.ps1` | Фоновая нагрузка на sandbox | 5, 9 |
| `up-all.ps1` | Всё с нуля одной командой | 10 |

Каждый скрипт должен быть идемпотентным: повторный запуск не ломает окружение.
