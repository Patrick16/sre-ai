#Requires -Version 7
# Platform layer: namespaces, Ingress Controller (Traefik), cert-manager and the private CA.
# Owned by the "platform team"; app releases (scripts/deploy-apps.ps1) assume all of this exists.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$root     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$values   = Join-Path $root 'infra' 'helm' 'values'
$platform = Join-Path $root 'infra' 'manifests' 'platform'

function Invoke-Checked([scriptblock]$Command, [string]$What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed" }
}

Invoke-Checked { kubectl apply -f (Join-Path $platform 'namespace-sandbox.yaml') } 'namespaces'

Invoke-Checked { helm repo add traefik https://traefik.github.io/charts --force-update | Out-Null } 'helm repo add traefik'
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

# Export the root CA so clients can verify the chain (curl --cacert, or trusting it in the OS yourself).
$out = Join-Path $root '.local'
New-Item -ItemType Directory -Force $out | Out-Null
$caFile = Join-Path $out 'sandbox-root-ca.crt'
$b64 = kubectl get secret sandbox-root-ca -n cert-manager -o jsonpath='{.data.ca\.crt}'
[IO.File]::WriteAllBytes($caFile, [Convert]::FromBase64String($b64))
Write-Host "Root CA exported to $caFile"
