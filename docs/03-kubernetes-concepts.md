# 03. Kubernetes concepts — what and why

For each object: **what it is**, **what problem it solves**, **how we use it in the project**,
and **what to look at by hand**. Read it as you go through the roadmap phases.

## How Kubernetes works at all (in 1 minute)

Kubernetes is a system to which you describe the **desired state** ("I want 3 copies of
service X with such-and-such configuration"), and it constantly brings the **actual state**
to the desired one. This mechanism is called the **reconcile loop**:
a controller looks at the difference between "want" and "have" and takes a step toward "want". Remember this idea —
our agent is built in a similar way (see [05](05-agent-architecture.md)).

The main parts of the cluster:
- **Control plane**: `kube-apiserver` (the single door into the cluster — everything, including kubectl
  and our agent, goes through it), `etcd` (the database where the desired state is stored),
  `scheduler` (decides on which node to place a pod), `controller-manager` (a set of reconcile loops).
- **Worker nodes**: `kubelet` (runs containers on the node), `kube-proxy` (network rules for Services),
  a container runtime (containerd).

---

## Pod
- **What**: the smallest unit of execution — one or more containers with a shared network and volumes.
- **Problem**: containers that must live together (the application + the mesh sidecar proxy).
- **In the project**: we don't create pods directly — Deployments/StatefulSets create them.
- **By hand**: `kubectl get pods -n sandbox -o wide`, `kubectl describe pod ...`, `kubectl logs ...`.

## Deployment (and ReplicaSet)
- **What**: describes "N identical pods from such-and-such template" and manages updates.
- **Problem**: a pod dies — who brings it back? How do you roll out a new version without downtime?
  A Deployment creates a ReplicaSet, which maintains the number of pods; on update — a rolling update.
- **In the project**: frontend, orders-api, worker, the agent.
- **By hand**: delete a pod and watch it get recreated; `kubectl rollout status/history/undo`.

## StatefulSet
- **What**: like a Deployment, but pods have stable names (`postgres-0`) and their own volume.
- **Problem**: a database cannot simply be "replaced by an identical copy" — it has data and identity.
- **In the project**: PostgreSQL.

## Service
- **What**: a stable address (a DNS name + a virtual IP) for a group of pods selected by labels.
- **Problem**: pods are mortal, their IPs change. A Service provides a permanent name `orders-api.sandbox.svc.cluster.local`
  and balances the load.
- **Types**: `ClusterIP` (only inside the cluster — the default), `NodePort` (a port on each node),
  `LoadBalancer` (an external cloud load balancer), `Headless` (no virtual IP — for StatefulSets).
- **In the project**: ClusterIP for all services; from outside we only enter through the Ingress.

## Ingress and the Ingress Controller
- **What**: Ingress — HTTP routing rules (host/path → Service). The Ingress Controller
  (Traefik for us) is the real proxy that executes these rules.
- **Problem**: don't create a LoadBalancer for each service; a single entry point with TLS, routing by host/path.
- **In the project**: `https://sandbox.localhost` → frontend, `/api` → orders-api. In kind we forward ports 80/443 from the host.
  Practice — [11-phase-2-ingress-tls.md](11-phase-2-ingress-tls.md).
- **Important**: an Ingress object does nothing without an installed controller. This is a common trap.
- **Next**: Gateway API — a newer replacement for Ingress; you can try it in phase 10.

## ConfigMap
- **What**: non-secret configuration (key-value pairs or files).
- **Problem**: not rebuilding the image to change a setting; one image — different environments.
- **Attaching**: as environment variables or as files in a volume. The catch: env values are not updated
  in a running pod; files are updated (with a delay), but the application must re-read them.
- **In the project**: service settings, "fault modes", the agent's configuration (the action allowlist).

## Secret
- **What**: the same as a ConfigMap, but for sensitive data.
- **Important to understand**: by default a Secret is **base64, not encryption**. Protection comes from
  RBAC (who can read it), encryption of etcd at rest, and external stores.
- **How it is done in production**: Sealed Secrets, External Secrets Operator + Vault/a cloud KMS, SOPS.
- **In the project**: the database password (`postgres-credentials`) and the service API key (`sandbox-api-key`); `deploy-apps.ps1`
  generates both once. LLM provider keys are held by the agent only in memory (typed into the console) or in a Secret.
  Later — try Sealed Secrets, so the secrets can be kept in git.

## Probes (liveness / readiness / startup)
- **liveness**: "is the process alive?" — if not, the kubelet restarts the container.
- **readiness**: "is it ready to take traffic?" — if not, the pod is removed from the Service (but not restarted).
- **startup**: gives a slow application time to start before liveness kicks in.
- **Trap**: a liveness probe that checks a dependency (the DB) leads to cascading restarts. Liveness is only about the process itself.

## Resources: requests and limits
- **requests**: how much CPU/memory is guaranteed — the scheduler uses this to choose a node.
- **limits**: the ceiling. Exceeding memory → `OOMKilled`; exceeding CPU → throttling (slowdown).
- **In the project**: mandatory for all; the "memory leak" mode demonstrates OOMKilled.

## Persistent Volumes: PV, PVC, StorageClass
- **PV** (PersistentVolume): a real piece of storage in the cluster.
- **PVC** (PersistentVolumeClaim): a request "I need 1Gi with RWO access mode".
- **StorageClass**: a "storage type" and how to create it automatically (dynamic provisioning).
- **Problem**: a container's filesystem disappears together with the container.
- **In kind**: the StorageClass `standard` (local-path-provisioner) — data lives on the node's disk.
  In the cloud, a network disk (EBS, Azure Disk) would take its place.
- **In the project**: PostgreSQL, the storage of Prometheus/Loki, the agent's reports.

## Namespace
- A logical partition of the cluster. A boundary for RBAC, ResourceQuota, NetworkPolicy. Not a security boundary by itself.

## RBAC: ServiceAccount, Role, RoleBinding
- **ServiceAccount** — the "account" for a pod. **Role** — what is allowed (verbs on resources in a namespace).
  **RoleBinding** — who gets the Role. ClusterRole/ClusterRoleBinding — the same, cluster-wide.
- **In the project**: the agent's key protection is that its ServiceAccount can read almost everything but
  change only specific things in `sandbox`. Check: `kubectl auth can-i --as=system:serviceaccount:sre-agent:sre-agent ...`.

## NetworkPolicy
- **What**: a firewall between pods, selected by labels/namespace.
- **Problem**: by default in Kubernetes any pod can reach any other pod.
- **Important**: it only works if the CNI supports it (kind's default CNI, kindnet, supports NetworkPolicy in recent versions;
  otherwise install Calico/Cilium).
- **In the project**: default-deny in `sandbox` and `sre-agent`; each chart explicitly allows its own traffic
  (frontend→orders-api→postgres, scrapes from `monitoring`, Alertmanager→agent).

## Helm
- **Chart** — a package of manifest templates. **Values** — parameters. **Release** — an installed instance of a chart.
- **Problem**: dozens of almost identical YAML files per service and environment; versioning and rollbacks.
- **In the project**: our shared `sandbox-service` chart for all our services, plus community charts
  for the infrastructure (traefik, cert-manager, kube-prometheus-stack, loki, alloy, linkerd, chaos-mesh); PostgreSQL has its own chart `infra/helm/postgres`.
- **By hand**: `helm template` (see what would be produced), `helm diff` (plugin), `helm rollback`.

## TLS and mTLS
- **TLS**: the client verifies the server by its certificate, and the traffic is encrypted. This is how HTTPS works on the Ingress.
- **mTLS** (mutual TLS): **both** sides present certificates. The server knows *which exact service* came to it —
  the basis of zero trust inside the cluster.
- **Problem**: issuing, distributing and rotating certificates by hand for every service is hell. A mesh solves this.

## Service Mesh (Linkerd)
- **What**: a sidecar proxy is added to each pod; all of the service's traffic goes through it.
  The control plane gives the proxies certificates and configuration.
- **What it gives without code changes**: automatic mTLS with certificate rotation, metrics
  (success rate, latency) per connection, retries, timeouts, traffic split (canary).
- **Linkerd vs Istio**: Linkerd is simpler and lighter — for learning it's better to start with it. Istio is more powerful
  (and more complex); understanding the difference is the task of phase 6.
- **Cost**: extra latency and resources per pod, one more component that can break.
- **In the project**: Linkerd edge, mTLS for `sandbox` and `sre-agent`; the practice and the pitfalls (port 4143, trust roots,
  Pod Security) — [15-phase-6-network-security.md](15-phase-6-network-security.md).

## CRDs and operators
- **CRD** (Custom Resource Definition) — a way to add your own object types to Kubernetes
  (`Certificate`, `ServiceMonitor`, `PodChaos`). An **operator** is a controller that processes them.
- Almost all the "production" components (cert-manager, prometheus-operator, Chaos Mesh) are built this way.
  See them with: `kubectl get crd`.
