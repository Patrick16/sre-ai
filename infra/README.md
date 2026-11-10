# infra/

Всё, что описывает кластер и то, что в нём развёрнуто. Папки заполняются по фазам [роадмапа](../docs/01-roadmap.md).

| Папка | Что внутри | Фаза |
|---|---|---|
| `cluster/` ✅ | Конфиг kind (`kind-config.yaml`: 1 control-plane + 2 worker, порты 80/443 только на 127.0.0.1, зеркала реестров для containerd) | 0, 5 |
| `manifests/platform/` ✅ | То, что принадлежит платформе, а не приложению: namespaces с Pod Security (`sandbox`, `sre-agent` — restricted и в mesh; `monitoring` — privileged), CA-issuers, default-deny NetworkPolicy, identity Linkerd (cert-manager). По одному файлу на namespace (`namespace-sandbox/monitoring/sre-agent/chaos-mesh.yaml`). Сырые манифесты приложений Фаз 1–2 заменены чартом (остались в истории git: `8a47ee6`, `95f2f88`) | 1–5 |
| `helm/sandbox-service/` ✅ | Наш чарт для .NET-сервисов и агента: Deployment, Service, Ingress, ServiceAccount, NetworkPolicy, HTTPRoute (retries/timeouts), PodMonitor, SLO-правила | 3, 5, 6 |
| `helm/postgres/` ✅ | Наш чарт PostgreSQL: StatefulSet, headless Service, PVC, postgres-exporter, `PostgresDown` | 4, 5 |
| `helm/loadgen/` ✅ | Фоновая нагрузка k6 через Traefik | 5 |
| `helm/sandbox-observability/` ✅ | Дашборды Grafana как код (`dashboards/*.json`) | 5 |
| `helm/releases/<namespace>/` ✅ | values отдельного релиза на каждый компонент; имя папки — namespace | 3–5 |
| `helm/values/` ✅ | values для чартов сообщества: traefik, cert-manager, kube-prometheus-stack, loki, alloy, linkerd (crds, cni, control-plane, viz); позже chaos-mesh | 2+ |
| `manual/` | Необязательный «ручной режим» Фазы 1: те же голые манифесты для `kubectl apply` в **отдельном** namespace `sandbox-manual` (образы 0.1.0, без Helm/mesh/БД). Рабочий деплой проекта — только Helm; этот каталог ему не мешает, см. [manual/README.md](manual/README.md) | 1 |
| `chaos/` | Манифесты экспериментов Chaos Mesh (всегда ограничены namespace `sandbox`) | 9 |

Правила:
- в манифестах и values — короткие комментарии «зачем», а не «что»;
- секреты не коммитятся в открытом виде (генерируются скриптами; позже — Sealed Secrets);
- версии чартов сообщества фиксируются явно.
