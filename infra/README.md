# infra/

Всё, что описывает кластер и то, что в нём развёрнуто. Сейчас папки пустые —
они заполняются по фазам [роадмапа](../docs/01-roadmap.md).

| Папка | Что будет | Фаза |
|---|---|---|
| `cluster/` ✅ | Конфиг kind (`kind-config.yaml`: 1 control-plane + 2 worker, проброс портов 80/443 для Ingress) | 0 |
| `manifests/platform/` ✅ | То, что принадлежит платформе, а не приложению: namespace с Pod Security, CA-issuers. Сырые манифесты приложений Фаз 1–2 заменены чартом (остались в истории git: `8a47ee6`, `95f2f88`) | 1–3 |
| `helm/sandbox-service/` ✅ | Наш чарт для всех sandbox-сервисов | 3 |
| `helm/postgres/` ✅ | Наш чарт PostgreSQL: StatefulSet, headless Service, PVC | 4 |
| `helm/releases/sandbox/` ✅ | values отдельного релиза на каждый компонент (включая `postgres.yaml`) | 3–4 |
| `helm/values/` ✅ | values для чартов сообщества (traefik ✅, cert-manager ✅, позже kube-prometheus-stack, loki, linkerd, chaos-mesh) | 2+ |
| `chaos/` | Манифесты экспериментов Chaos Mesh (всегда ограничены namespace `sandbox`) | 9 |

Правила:
- в манифестах и values — короткие комментарии «зачем», а не «что»;
- секреты не коммитятся в открытом виде (позже — Sealed Secrets);
- версии чартов сообщества фиксируются явно.
