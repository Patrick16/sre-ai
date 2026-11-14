# scripts/

Scripts for working with the environment. Written in PowerShell (the main OS is Windows),
cross-platform where possible (`pwsh`).

| Script (planned) | What it does | Phase |
|---|---|---|
| `cluster-up.ps1` / `cluster-down.ps1` ✅ | Create / delete the kind cluster `sre-ai` (`cluster-down` does not touch the image caches) | 0 |
| `registry-cache.ps1` ✅ | Pull-through caches for docker.io / quay.io / registry.k8s.io (called from `cluster-up`; `-Remove`, `-Purge`) | 5 |
| `build-images.ps1` ✅ | Build the .NET images (services + `sre-agent`) and load them into kind; the tag is read from `infra/helm/releases/<ns>/<service>.yaml` (`-Tag` overrides it) | 1, 5 |
| `deploy-infra.ps1` ✅ | Platform: namespaces, kube-prometheus-stack, Traefik, cert-manager, CA issuers, Loki, Alloy, dashboards; exports the root CA to `.local/` | 2–5 |
| `deploy-apps.ps1` ✅ | Secrets (once) + Helm releases: postgres → orders-api → worker → frontend → loadgen → sre-agent (`-Services`), `--rollback-on-failure` | 3–5 |
| `grafana-password.ps1` ✅ | Show the generated `admin` password for Grafana | 5 |
| `port-forward.ps1` ✅ | Forward Prometheus/Loki/Alertmanager to localhost for running the agent locally (`dotnet run`, Development environment) | 7 |
| `up-all.ps1` | Everything from scratch in one command | 10 |

Every script must be idempotent: running it again must not break the environment.
