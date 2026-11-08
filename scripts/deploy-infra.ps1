#Requires -Version 7
# Platform layer: namespaces, monitoring (Prometheus/Alertmanager/Grafana, Loki, Alloy), Ingress Controller
# (Traefik), cert-manager and the private CA. Owned by the "platform team"; app releases
# (scripts/deploy-apps.ps1) assume all of this exists.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$root     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$helm     = Join-Path $root 'infra' 'helm'
$values   = Join-Path $helm 'values'
$platform = Join-Path $root 'infra' 'manifests' 'platform'

function Invoke-Checked([scriptblock]$Command, [string]$What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed" }
}

Invoke-Checked { kubectl apply -f (Join-Path $platform 'namespace-sandbox.yaml') -f (Join-Path $platform 'namespace-monitoring.yaml') } 'namespaces'
# Default-deny + DNS for the app namespaces; each app chart adds its own allow rules.
Invoke-Checked { kubectl apply -f (Join-Path $platform 'networkpolicy-baseline.yaml') } 'network policy baseline'
# Phase 8: the API server limits what the agent's ServiceAccount may change (only restarts and Deployment-pod deletes).
Invoke-Checked { kubectl apply -f (Join-Path $platform 'sre-agent-admission-policy.yaml') } 'sre-agent admission policy'

# Grafana admin password: generated once, never in git.
if (-not (kubectl get secret grafana-admin -n monitoring --ignore-not-found -o name)) {
    $pw = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLower()
    Invoke-Checked { kubectl create secret generic grafana-admin -n monitoring --from-literal=admin-user=admin --from-literal="admin-password=$pw" } 'grafana secret'
}

foreach ($repo in @(
        @('prometheus-community', 'https://prometheus-community.github.io/helm-charts'),
        @('grafana', 'https://grafana.github.io/helm-charts'),
        @('traefik', 'https://traefik.github.io/charts'),
        @('linkerd-edge', 'https://helm.linkerd.io/edge'))) {
    Invoke-Checked { helm repo add $repo[0] $repo[1] --force-update | Out-Null } "helm repo add $($repo[0])"
}

# First: it brings the CRDs (ServiceMonitor, PodMonitor, PrometheusRule) that everything below creates objects of.
Invoke-Checked {
    helm upgrade --install kube-prometheus-stack prometheus-community/kube-prometheus-stack --version 93.0.0 `
        -n monitoring -f (Join-Path $values 'kube-prometheus-stack.yaml') --wait --timeout 10m
} 'kube-prometheus-stack install'

Invoke-Checked {
    helm upgrade --install traefik traefik/traefik --version 42.3.1 `
        -n traefik --create-namespace -f (Join-Path $values 'traefik.yaml') --wait --timeout 5m
} 'traefik install'

Invoke-Checked {
    helm upgrade --install cert-manager oci://quay.io/jetstack/charts/cert-manager --version v1.21.3 `
        -n cert-manager --create-namespace -f (Join-Path $values 'cert-manager.yaml') --wait --timeout 5m
} 'cert-manager install'

Invoke-Checked { kubectl apply -f (Join-Path $platform 'cluster-issuers.yaml') } 'cluster issuers'
Invoke-Checked { kubectl wait -n cert-manager certificate/sandbox-root-ca --for=condition=Ready --timeout=120s } 'root CA'
Invoke-Checked { kubectl wait clusterissuer/sandbox-ca --for=condition=Ready --timeout=60s } 'sandbox-ca issuer'

# --- Service mesh (Linkerd, open-source edge releases) ---
# Identity first: namespace + trust anchor + identity issuer, all issued and rotated by cert-manager.
Invoke-Checked { kubectl apply -f (Join-Path $platform 'linkerd-identity.yaml') } 'linkerd identity'
Invoke-Checked { kubectl wait -n linkerd certificate/linkerd-trust-anchor certificate/linkerd-identity-issuer --for=condition=Ready --timeout=120s } 'linkerd certificates'

Invoke-Checked {
    helm upgrade --install linkerd-crds linkerd-edge/linkerd-crds --version 2026.11.1 `
        -n linkerd -f (Join-Path $values 'linkerd-crds.yaml') --wait
} 'linkerd-crds install'
Invoke-Checked {
    helm upgrade --install linkerd-cni linkerd-edge/linkerd2-cni --version 2026.11.1 `
        -n linkerd-cni --create-namespace -f (Join-Path $values 'linkerd-cni.yaml') --wait --timeout 5m
} 'linkerd-cni install'

# Every proxy must be told which root to trust: the PUBLIC certificate of the trust anchor.
$anchorFile = Join-Path $root '.local' 'linkerd-trust-anchor.crt'
New-Item -ItemType Directory -Force (Split-Path $anchorFile) | Out-Null
$b64 = kubectl get secret linkerd-trust-anchor -n linkerd -o jsonpath='{.data.ca\.crt}'
[IO.File]::WriteAllBytes($anchorFile, [Convert]::FromBase64String($b64))
# With identity.externalCA the chart expects the trust bundle ConfigMap to be provided from outside
# (in production: trust-manager, which can also carry old+new roots during a root rotation).
Invoke-Checked {
    kubectl create configmap linkerd-identity-trust-roots -n linkerd --from-file="ca-bundle.crt=$anchorFile" `
        --dry-run=client -o yaml | kubectl apply -f -
} 'linkerd trust roots'
Invoke-Checked {
    helm upgrade --install linkerd-control-plane linkerd-edge/linkerd-control-plane --version 2026.11.1 `
        -n linkerd -f (Join-Path $values 'linkerd-control-plane.yaml') `
        --set-file "identityTrustAnchorsPEM=$($anchorFile.Replace('\', '/'))" `
        --wait --timeout 5m
} 'linkerd control plane install'
Invoke-Checked {
    helm upgrade --install linkerd-viz linkerd-edge/linkerd-viz --version 2026.11.1 `
        -n linkerd-viz --create-namespace -f (Join-Path $values 'linkerd-viz.yaml') --wait --timeout 5m
} 'linkerd-viz install'

Invoke-Checked {
    helm upgrade --install loki grafana/loki --version 7.5.0 `
        -n monitoring -f (Join-Path $values 'loki.yaml') --wait --timeout 5m
} 'loki install'

Invoke-Checked {
    helm upgrade --install alloy grafana/alloy --version 1.15.0 `
        -n monitoring -f (Join-Path $values 'alloy.yaml') --wait --timeout 5m
} 'alloy install'

Invoke-Checked {
    helm upgrade --install sandbox-observability (Join-Path $helm 'sandbox-observability') -n monitoring --wait
} 'dashboards'

# Export the root CA so clients can verify the chain (curl --cacert, or trusting it in the OS yourself).
$out = Join-Path $root '.local'
New-Item -ItemType Directory -Force $out | Out-Null
$caFile = Join-Path $out 'sandbox-root-ca.crt'
$b64 = kubectl get secret sandbox-root-ca -n cert-manager -o jsonpath='{.data.ca\.crt}'
[IO.File]::WriteAllBytes($caFile, [Convert]::FromBase64String($b64))
Write-Host "Root CA exported to $caFile"
Write-Host 'Grafana: https://grafana.localhost  (user admin, password: ./scripts/grafana-password.ps1)'
