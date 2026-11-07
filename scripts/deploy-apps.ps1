#Requires -Version 7
# App layer: one Helm release per sandbox service, all from the same chart (infra/helm/sandbox-service).
# Assumes scripts/deploy-infra.ps1 has run and images are loaded (scripts/build-images.ps1).
[CmdletBinding()]
param(
    [string[]]$Services = @('orders-api', 'worker', 'frontend')
)
$ErrorActionPreference = 'Stop'

$root     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$chart    = Join-Path $root 'infra' 'helm' 'sandbox-service'
$releases = Join-Path $root 'infra' 'helm' 'releases' 'sandbox'

# The Secret is generated here (random value) so it never lands in git.
if (-not (kubectl get secret sandbox-api-key -n sandbox --ignore-not-found -o name)) {
    $key = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower()
    kubectl create secret generic sandbox-api-key -n sandbox --from-literal="api-key=$key"
    if ($LASTEXITCODE -ne 0) { throw 'creating secret failed' }
}

foreach ($service in $Services) {
    $valuesFile = Join-Path $releases "$service.yaml"
    if (-not (Test-Path $valuesFile)) { throw "No values file for '$service' at $valuesFile" }

    # --rollback-on-failure: if pods don't become ready in time, Helm reverts to the last good revision.
    helm upgrade --install $service $chart -n sandbox -f $valuesFile --wait --rollback-on-failure --timeout 5m
    if ($LASTEXITCODE -ne 0) { throw "helm upgrade $service failed (rolled back)" }
}

if ($Services -contains 'frontend') {
    kubectl wait -n sandbox certificate/sandbox-tls --for=condition=Ready --timeout=120s
    if ($LASTEXITCODE -ne 0) { throw 'sandbox-tls certificate not ready' }
}

helm list -n sandbox
