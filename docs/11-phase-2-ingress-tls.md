# 11. Phase 2: Ingress and TLS in practice

The theory — the sections "Ingress and the Ingress Controller" and "TLS and mTLS" in [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Why Traefik, not ingress-nginx

Originally the plan was ingress-nginx — the most common controller. But in November 2025
Kubernetes announced its retirement, and from March 2026 ingress-nginx has **no releases and no security fixes**.
Existing installations keep working, but a new "production" cluster must not install it.
The project's recommendation is to move to the **Gateway API**
(sources: [the announcement](https://www.kubernetes.io/blog/2025/11/11/ingress-nginx-retirement/),
[the statement of the Steering and Security Response Committees](https://www.kubernetes.io/blog/2026/01/29/ingress-nginx-statement/)).

This is a good lesson in itself: **an infrastructure component can "die"**, and an SRE must watch the
lifecycle of dependencies, not only their uptime.

We chose **Traefik**: it is actively maintained and understands both the classic `Ingress` and the Gateway API.
First we learn Ingress (you will still meet it everywhere), then Gateway API on the same controller.

## What is deployed

| Component | How | Where |
|---|---|---|
| Traefik v3.7 (chart 42.3.1) | Helm | namespace `traefik`, [values](../infra/helm/values/traefik.yaml) |
| cert-manager v1.21.3 | Helm (OCI chart) | namespace `cert-manager`, [values](../infra/helm/values/cert-manager.yaml) |
| Our own CA: `selfsigned` → `sandbox-root-ca` → ClusterIssuer `sandbox-ca` | manifest | [cluster-issuers.yaml](../infra/manifests/platform/cluster-issuers.yaml) |
| Ingress `frontend` (`/`) and `orders-api` (`/api`), a Middleware for `/api` | from Phase 3 — the chart template | [ingress.yaml](../infra/helm/sandbox-service/templates/ingress.yaml); the original manifest: `git show 95f2f88:infra/manifests/phase-2/02-ingress.yaml` |

```powershell
./scripts/deploy-infra.ps1      # Traefik, cert-manager, CA, export of the root CA to .local/
./scripts/deploy-apps.ps1       # the Ingresses are created by the Helm releases of frontend and orders-api
```

> From Phase 3 the Middleware is called `orders-api-strip-prefix` (not `strip-api`) — the chart creates it.

Infrastructure components are installed **with ready-made community Helm charts** right away: writing their manifests
by hand makes no sense. Our own chart for our services is the topic of Phase 3.

## The request path

```
curl https://sandbox.localhost/        (*.localhost → 127.0.0.1, no hosts-file edit needed)
  │
  ▼  port 443 on Windows
Docker: forwarding 443 → the node container sre-ai-control-plane   (extraPortMappings in kind-config.yaml)
  │
  ▼  hostPort 443 on this node
Traefik pod  ── TLS is terminated here (the certificate from the secret sandbox-tls)
  │  picks the router by Host + Path: "/" → frontend, "/api" → orders-api (+ strip "/api")
  │  takes the pod addresses from EndpointSlices and goes DIRECTLY to the pod, bypassing the ClusterIP
  ▼
frontend pod 10.244.x.x:8080   (already over plain HTTP inside the cluster)
```

Important details:
- **The Ingress has an empty `ADDRESS`** in `kubectl get ingress` — this is normal: Traefik's Service is of type `ClusterIP`,
  there is no load balancer. In the cloud, the IP of a cloud LoadBalancer would be here.
- **TLS only up to Traefik.** Inside the cluster the traffic is not encrypted yet. Phase 6 (mTLS) will close this.
- **Traefik is the only pod with a hostPort.** Hence `maxSurge: 0` in the values: a new pod cannot take the port
  while the old one is alive, so during a Traefik update there is a short outage. In production the controller runs in several
  replicas behind a cloud load balancer, and this problem does not exist.

## How cert-manager works

```
Ingress (annotation cert-manager.io/cluster-issuer: sandbox-ca, tls.secretName: sandbox-tls)
   └─► cert-manager creates the Certificate sandbox-tls
          └─► CertificateRequest ─► ClusterIssuer sandbox-ca signs it with its CA key
                 └─► Secret sandbox-tls (tls.crt, tls.key, ca.crt) ─► Traefik picks it up automatically
```

What it looked like in practice (`kubectl get secret sandbox-tls -n sandbox`):
- `Issuer = CN=sre-ai sandbox root CA`, **the `Subject` is empty** — the host name is only in the **SAN** (`DNS=sandbox.localhost`).
  Modern clients look only at the SAN; the CN in server certificates is obsolete.
- Lifetime 90 days, `renewalTime` — at 2/3 of the lifetime. cert-manager **renews the certificate itself**. Manual renewal
  of certificates is a classic cause of real incidents ("the certificate expired on a Saturday night").

The `orders-api` Ingress uses the **same** secret, but without the cert-manager annotation: otherwise two Certificates would fight over one secret.

## What we found during verification

### 1. HTTP → HTTPS redirect
`curl http://sandbox.localhost/` → `301 → https://sandbox.localhost/`. Configured on the `web` entrypoint in the Traefik values.

### 2. "Trust" and "revocation check" are different things
`curl.exe --cacert .local/sandbox-root-ca.crt https://sandbox.localhost/` on Windows failed with
`schannel: the revocation status is unknown`. The trust chain is in order: Schannel (the Windows TLS stack)
additionally tries to check whether the certificate has been **revoked**, and our CA has no CRL/OCSP.
With `--ssl-no-revoke` — `200`. Without our CA — `SEC_E_UNTRUSTED_ROOT`, as it should be.
Public CAs (Let's Encrypt) have a revocation mechanism; internal CAs often do not — and this is how it shows up.

### 3. Ingress balances per request, but only "at the entrance"
10 requests to `/`: **5/5** between the two frontend pods — unlike Phase 1, where everything went to one pod.
Traefik is an L7 proxy and picks a pod for each request. But inside:
```
5x  frontend-...-f75zn -> orders-api-...-t9c9j (1 orders)
5x  frontend-...-fg8mc -> orders-api-...-4q95p (2 orders)
```
Each frontend pod still keeps a keep-alive connection to "its" orders-api pod. The Ingress balances
only the external traffic. For service→service traffic you need a service mesh (Phase 6).

### 4. No backends → 503
`kubectl scale deploy/frontend --replicas=0` → Traefik answers `503 Service Unavailable`. This is an excellent
signal for alerts (Phase 5): a rise in 5xx at the ingress is one of the first signs of a problem.

## Browser

Open https://sandbox.localhost/ — the browser will warn that the certificate is not trusted. This is correct:
nobody knows our CA. Options:
- simply accept the warning (enough for a sandbox);
- **yourself** add `.local/sandbox-root-ca.crt` to your user's trusted root certificates
  (`certmgr.msc` → "Trusted Root Certification Authorities" → Import). This changes the system's security settings —
  do it consciously: anyone who obtains this CA's key (it lies in the cluster's secret) can issue
  a certificate for any site your browser would trust. After the experiments, remove the certificate from the store.
  Firefox uses its own certificate store.

## The Traefik dashboard

```powershell
kubectl port-forward -n traefik deploy/traefik 9000:8080
# http://localhost:9000/dashboard/
```
You see every router (the Host/Path rule), its middleware and the backend service. The dashboard is published only on the
internal `traefik` entrypoint — it is not exposed outside (it must not be exposed without authentication).

## Ingress vs Gateway API — why it matters

Notice `traefik.ingress.kubernetes.io/router.middlewares: sandbox-strip-api@kubernetescrd`.
The Ingress specification **cannot** strip a path prefix — each controller does this with its own annotations and CRDs.
Moving from one controller to another means rewriting all the annotations. This is exactly the problem the **Gateway API** solves:
`Gateway` (what we listen to, whose controller it is) and `HTTPRoute` (routes, rewriting of paths and headers, traffic
splitting) are standard objects, the same for all controllers. Exercise 7 below.

## Exercises

**Project snapshot at this phase** — the tag `phase-2` (commit `95f2f88`: raw manifests + Traefik). It was not re-run to verify —
it is the state the exercises were composed for.

```powershell
git worktree add ../sre-ai-phase-2 phase-2   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-2
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-phase1.ps1
./scripts/deploy-phase2.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-2 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1`. Exercises 3–4 and 6 change the Ingress/Certificate,
exercise 7 — the Traefik values (the platform layer). Rollback:

```powershell
git restore infra/                               # if you edited the values files
./scripts/reset-exercise.ps1 -Level Platform     # platform + applications, data is kept
```

| # | Do | What to expect |
|---|---|---|
| 1 | `kubectl describe certificate sandbox-tls -n sandbox`, then `kubectl get certificaterequest -n sandbox` | The chain of cert-manager objects and their statuses |
| 2 | `kubectl delete secret sandbox-tls -n sandbox`, after 10 s `get secret` again | cert-manager re-issued the certificate by itself (a new serial) |
| 3 | Copy the Certificate with `duration: 1h`, `renewBefore: 50m` for a new host and observe | Renewal every ~10 minutes — this is how rotation is tested |
| 4 | Add a host-based route: `api.sandbox.localhost` → orders-api without strip, add the host to `tls.hosts` | cert-manager re-issues the certificate with two SANs |
| 5 | `kubectl delete pod -n traefik -l app.kubernetes.io/name=traefik` and immediately curl in a loop | A short outage: Traefik is the single entry point (SPOF) |
| 6 | Set a non-existent `ingressClassName: nginx` on the Ingress | Traefik ignores the Ingress → 404. A common mistake during a migration |
| 7 | Enable `providers.kubernetesGateway.enabled: true` in the values and describe the same route via `Gateway` + `HTTPRoute` (with `URLRewrite` instead of a Middleware) | The same result with standard objects, no Traefik annotations |
| 8 | Watch `kubectl logs -n traefik deploy/traefik -f` during requests | The access log: router, the backend pod address, response time |

## Debugging cheat sheet

| Symptom | Where to look | Common cause |
|---|---|---|
| `404 page not found` from Traefik | The dashboard → routers | No matching router: the host/path/`ingressClassName` do not match |
| `503` | `kubectl get endpointslices -n sandbox` | The Service has no ready pods |
| `502`/`504` | The Traefik access log, the pod's logs | The pod answers with an error / does not answer in time |
| The "wrong" certificate / Traefik's default | `kubectl get certificate -A` | The Certificate is not Ready, the secret was not created, the host is not in `tls.hosts` |
| Certificate stuck at `False` | `kubectl describe certificaterequest`, the `cert-manager` logs | The Issuer is not Ready, a typo in the issuer name |
| `curl: (60) revocation status is unknown` (Windows) | — | The internal CA has no CRL → `--ssl-no-revoke` |
| `ERR_CONNECTION_REFUSED` on 80/443 | `docker ps` (the node's port forwarding) | The cluster was created without `extraPortMappings`, or the port is taken on the host |
