# infra/

Всё, что описывает кластер и то, что в нём развёрнуто. Сейчас папки пустые —
они заполняются по фазам [роадмапа](../docs/01-roadmap.md).

| Папка | Что будет | Фаза |
|---|---|---|
| `cluster/` | Конфиг kind (`kind-config.yaml`: 1 control-plane + 2 worker, проброс портов 80/443 для Ingress) | 0 |
| `manifests/` | «Сырые» YAML, разложенные по фазам (`phase-1/`, `phase-2/`…) — для понимания механики до Helm | 1–2 |
| `helm/` | Свой чарт `sandbox-service`, чарт агента и values-файлы для инфраструктурных чартов сообщества (ingress-nginx, cert-manager, kube-prometheus-stack, loki, linkerd, chaos-mesh, postgresql) | 3+ |
| `chaos/` | Манифесты экспериментов Chaos Mesh (всегда ограничены namespace `sandbox`) | 9 |

Правила:
- в манифестах и values — короткие комментарии «зачем», а не «что»;
- секреты не коммитятся в открытом виде (позже — Sealed Secrets);
- версии чартов сообщества фиксируются явно.
