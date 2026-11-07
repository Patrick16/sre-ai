#Requires -Version 7
# App layer: one Helm release per component in namespace sandbox.
# Assumes scripts/deploy-infra.ps1 has run and images are loaded (scripts/build-images.ps1).
[CmdletBinding()]
param(
    # Order matters: the database first, then its clients.
    [string[]]$Services = @('postgres', 'orders-api', 'worker', 'frontend')
)
$ErrorActionPreference = 'Stop'

$root     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releases = Join-Path $root 'infra' 'helm' 'releases' 'sandbox'
$charts   = @{ 'postgres' = Join-Path $root 'infra' 'helm' 'postgres' }
$default  = Join-Path $root 'infra' 'helm' 'sandbox-service'

# Secrets are generated here (random values) so they never land in git. Existing ones are kept:
# regenerating the DB password would lock the apps out of an already-initialised database.
function New-SecretIfMissing([string]$Name, [string]$Key) {
    if (kubectl get secret $Name -n sandbox --ignore-not-found -o name) { return }
    $value = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower()
    kubectl create secret generic $Name -n sandbox --from-literal="$Key=$value"
    if ($LASTEXITCODE -ne 0) { throw "creating secret $Name failed" }
}
New-SecretIfMissing 'sandbox-api-key' 'api-key'
New-SecretIfMissing 'postgres-credentials' 'password'

foreach ($service in $Services) {
    $valuesFile = Join-Path $releases "$service.yaml"
    if (-not (Test-Path $valuesFile)) { throw "No values file for '$service' at $valuesFile" }
    $chart = $charts[$service] ?? $default

    # --rollback-on-failure: if pods don't become ready in time, Helm reverts to the last good revision.
    helm upgrade --install $service $chart -n sandbox -f $valuesFile --wait --rollback-on-failure --timeout 5m
    if ($LASTEXITCODE -ne 0) { throw "helm upgrade $service failed (rolled back)" }
}

if ($Services -contains 'frontend') {
    kubectl wait -n sandbox certificate/sandbox-tls --for=condition=Ready --timeout=120s
    if ($LASTEXITCODE -ne 0) { throw 'sandbox-tls certificate not ready' }
}

helm list -n sandbox
