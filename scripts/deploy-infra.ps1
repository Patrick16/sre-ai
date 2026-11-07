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

# Grafana admin password: generated once, never in git.
if (-not (kubectl get secret grafana-admin -n monitoring --ignore-not-found -o name)) {
    $pw = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLower()
    Invoke-Checked { kubectl create secret generic grafana-admin -n monitoring --from-literal=admin-user=admin --from-literal="admin-password=$pw" } 'grafana secret'
}

foreach ($repo in @(
        @('prometheus-community', 'https://prometheus-community.github.io/helm-charts'),
        @('grafana', 'https://grafana.github.io/helm-charts'),
        @('traefik', 'https://traefik.github.io/charts'))) {
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
