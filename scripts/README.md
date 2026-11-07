# scripts/

Скрипты для работы с окружением. Пишутся на PowerShell (основная ОС — Windows),
по возможности кроссплатформенно (`pwsh`).

| Скрипт (план) | Что делает | Фаза |
|---|---|---|
| `cluster-up.ps1` / `cluster-down.ps1` ✅ | Создать / удалить kind-кластер `sre-ai` (кеши образов `cluster-down` не трогает) | 0 |
| `registry-cache.ps1` ✅ | Pull-through кеши образов для docker.io / quay.io / registry.k8s.io (вызывается из `cluster-up`; `-Remove`, `-Purge`) | 5 |
| `build-images.ps1` ✅ | Собрать .NET-образы (сервисы + `sre-agent`) и загрузить в kind; тег берётся из `infra/helm/releases/<ns>/<service>.yaml` (`-Tag` переопределяет) | 1, 5 |
| `deploy-infra.ps1` ✅ | Платформа: namespaces, kube-prometheus-stack, Traefik, cert-manager, CA-issuers, Loki, Alloy, дашборды; экспорт корневого CA в `.local/` | 2–5 |
| `deploy-apps.ps1` ✅ | Secrets (один раз) + Helm-релизы: postgres → orders-api → worker → frontend → loadgen → sre-agent (`-Services`), `--rollback-on-failure` | 3–5 |
| `grafana-password.ps1` ✅ | Показать сгенерированный пароль `admin` для Grafana | 5 |
| `port-forward.ps1` ✅ | Пробросить Prometheus/Loki/Alertmanager на localhost для локального запуска агента (`dotnet run`, окружение Development) | 7 |
| `up-all.ps1` | Всё с нуля одной командой | 10 |

Каждый скрипт должен быть идемпотентным: повторный запуск не ломает окружение.
