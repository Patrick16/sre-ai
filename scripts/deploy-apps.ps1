#Requires -Version 7
# App layer: one Helm release per component. Release values live in infra/helm/releases/<namespace>/<release>.yaml;
# the folder name is the namespace. Assumes scripts/deploy-infra.ps1 has run and images are loaded
# (scripts/build-images.ps1).
[CmdletBinding()]
param(
    # Order matters: the database first, then its clients, then traffic, then the agent.
    [string[]]$Services = @('postgres', 'orders-api', 'worker', 'frontend', 'loadgen', 'sre-agent'),
    # Take over fields that someone else (kubectl scale/edit...) changed by hand - used by reset-exercise.ps1.
    # Off by default on purpose: a conflict is a signal of drift (docs/12, exercise 8), not something to hide.
    [switch]$ForceConflicts
)
$ErrorActionPreference = 'Stop'

$root     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$helm     = Join-Path $root 'infra' 'helm'
$releases = Join-Path $helm 'releases'
$charts   = @{
    'postgres' = Join-Path $helm 'postgres'
    'loadgen'  = Join-Path $helm 'loadgen'
}
$default  = Join-Path $helm 'sandbox-service'

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
    $valuesFile = Get-ChildItem $releases -Recurse -Filter "$service.yaml" | Select-Object -First 1
    if (-not $valuesFile) { throw "No values file for '$service' under $releases" }
    $namespace = $valuesFile.Directory.Name
    $chart = $charts[$service] ?? $default

    # --rollback-on-failure: if pods don't become ready in time, Helm reverts to the last good revision.
    $extra = $ForceConflicts ? @('--force-conflicts') : @()
    helm upgrade --install $service $chart -n $namespace -f $valuesFile.FullName --wait --rollback-on-failure --timeout 5m @extra
    if ($LASTEXITCODE -ne 0) { throw "helm upgrade $service failed (rolled back)" }
}

if ($Services -contains 'frontend') {
    kubectl wait -n sandbox certificate/sandbox-tls --for=condition=Ready --timeout=120s
    if ($LASTEXITCODE -ne 0) { throw 'sandbox-tls certificate not ready' }
}

helm list -A --filter '^(postgres|orders-api|worker|frontend|loadgen|sre-agent)$'
