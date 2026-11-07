#Requires -Version 7
# Creates the local kind cluster (idempotent: does nothing if it already exists).
[CmdletBinding()]
param(
    [string]$Name = 'sre-ai'
)
$ErrorActionPreference = 'Stop'

foreach ($tool in 'docker', 'kind', 'kubectl') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' not found in PATH. See docs/09-dev-environment.md."
    }
}

docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop first.' }

$config = Join-Path $PSScriptRoot '..' 'infra' 'cluster' 'kind-config.yaml'

if ((kind get clusters) -contains $Name) {
    Write-Host "Cluster '$Name' already exists."
} else {
    kind create cluster --name $Name --config $config
    if ($LASTEXITCODE -ne 0) { throw 'kind create cluster failed.' }
}

# Pull-through image caches (survive cluster re-creation): see registry-cache.ps1.
& (Join-Path $PSScriptRoot 'registry-cache.ps1') -Cluster $Name

kubectl config use-context "kind-$Name" | Out-Null
kubectl wait --for=condition=Ready nodes --all --timeout=180s
kubectl get nodes -o wide
