#Requires -Version 7
# Applies the Phase 1 raw manifests. The Secret is generated here (random value) so it never lands in git.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$dir = Join-Path $PSScriptRoot '..' 'infra' 'manifests' 'phase-1'

kubectl apply -f (Join-Path $dir '00-namespace.yaml')

if (-not (kubectl get secret sandbox-api-key -n sandbox --ignore-not-found -o name)) {
    $key = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLower()
    kubectl create secret generic sandbox-api-key -n sandbox --from-literal="api-key=$key"
} else {
    Write-Host 'secret/sandbox-api-key already exists, keeping it'
}

kubectl apply -f $dir

foreach ($d in 'orders-api', 'frontend', 'worker') {
    kubectl rollout status "deployment/$d" -n sandbox --timeout=180s
    if ($LASTEXITCODE -ne 0) { throw "deployment/$d did not become ready" }
}
kubectl get pods -n sandbox -o wide
