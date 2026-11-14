# Manual run without Helm (Phase 1 manifests)

These are the original "bare" Phase 1 manifests (from commit `8a47ee6`), so that the sandbox can be brought up by hand with
`kubectl apply` and every object can be seen in full. The project's working deployment is still Helm — these files do not interfere with it:
everything lives in a **separate namespace `sandbox-manual`** (there is no mesh, default-deny NetworkPolicy, etc.), and the images are
`sandbox/*:0.1.0` (Helm uses `0.4.0`).

Differences from the original: only the namespace (`sandbox` → `sandbox-manual`). No PostgreSQL: orders-api version 0.1.0 keeps
orders in memory, so the images are built from the old commit, not from the current `src/`.

## Running

```powershell
./infra/manual/build-images.ps1                 # images 0.1.0 from the Phase 1 commit + kind load
kubectl apply -f infra/manual/phase-1/00-namespace.yaml
kubectl create secret generic sandbox-api-key -n sandbox-manual --from-literal="api-key=$([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower())"
kubectl apply -f infra/manual/phase-1/
kubectl get pods -n sandbox-manual -w
```

Check (Phase 1 has no Ingress — use port-forward):

```powershell
kubectl port-forward -n sandbox-manual svc/frontend 8080:80
curl.exe http://localhost:8080/
```

Remove everything: `kubectl delete namespace sandbox-manual`.

What to study: [docs/10-phase-1-kubernetes-basics.md](../../docs/10-phase-1-kubernetes-basics.md) (has the exercises).
