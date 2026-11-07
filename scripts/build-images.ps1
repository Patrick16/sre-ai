#Requires -Version 7
# Builds the sandbox service images and loads them into the kind cluster's nodes (no registry needed).
# The tag comes from each service's release values file (infra/helm/releases/sandbox/<service>.yaml),
# so the image that gets built is the one Helm will deploy. -Tag overrides it (e.g. to try a new version).
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$Cluster = 'sre-ai',
    [string[]]$Services = @('frontend', 'orders-api', 'worker')
)
$ErrorActionPreference = 'Stop'

$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root     = Join-Path $repo 'src' 'Sandbox.Services'
$releases = Join-Path $repo 'infra' 'helm' 'releases' 'sandbox'
$projects = @{
    'frontend'   = @{ Dir = 'Frontend';  Project = 'Sandbox.Frontend' }
    'orders-api' = @{ Dir = 'OrdersApi'; Project = 'Sandbox.OrdersApi' }
    'worker'     = @{ Dir = 'Worker';    Project = 'Sandbox.Worker' }
}

foreach ($service in $Services) {
    $p = $projects[$service] ?? (throw "Unknown service '$service'")
    $serviceTag = $Tag
    if (-not $serviceTag) {
        $match = Select-String -Path (Join-Path $releases "$service.yaml") -Pattern '^\s+tag:\s*"([^"]+)"' | Select-Object -First 1
        $serviceTag = $match.Matches[0].Groups[1].Value ?? (throw "No image tag in $service.yaml")
    }
    $image = "sandbox/${service}:$serviceTag"

    Write-Host "==> $image"
    docker build -f (Join-Path $root 'Dockerfile') --build-arg "PROJECT=$($p.Project)" -t $image (Join-Path $root $p.Dir)
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $service" }

    kind load docker-image $image --name $Cluster
    if ($LASTEXITCODE -ne 0) { throw "kind load failed for $service" }
}
