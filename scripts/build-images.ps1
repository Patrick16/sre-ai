#Requires -Version 7
# Builds the .NET images (sandbox services + the SRE agent) and loads them into the kind cluster's nodes
# (no registry needed). The tag comes from the release values file (infra/helm/releases/<namespace>/<name>.yaml),
# so the image that gets built is the one Helm will deploy. -Tag overrides it (e.g. to try a new version).
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$Cluster = 'sre-ai',
    [string[]]$Services = @('frontend', 'orders-api', 'worker', 'sre-agent')
)
$ErrorActionPreference = 'Stop'

$repo     = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src      = Join-Path $repo 'src'
$sandbox  = Join-Path $src 'Sandbox.Services'
$releases = Join-Path $repo 'infra' 'helm' 'releases'
$images = @{
    'frontend'   = @{ Image = 'sandbox/frontend';   Dockerfile = Join-Path $sandbox 'Dockerfile'; Context = $sandbox; Dir = 'Frontend';  Project = 'Sandbox.Frontend' }
    'orders-api' = @{ Image = 'sandbox/orders-api'; Dockerfile = Join-Path $sandbox 'Dockerfile'; Context = $sandbox; Dir = 'OrdersApi'; Project = 'Sandbox.OrdersApi' }
    'worker'     = @{ Image = 'sandbox/worker';     Dockerfile = Join-Path $sandbox 'Dockerfile'; Context = $sandbox; Dir = 'Worker';    Project = 'Sandbox.Worker' }
    # Sandbox services: context = src/Sandbox.Services (they share Shared/Faults.cs). The agent references the other SreAgent.* projects, so its build context is the whole src/.
    'sre-agent'  = @{ Image = 'sre-agent/host';     Dockerfile = Join-Path $src 'SreAgent.Host' 'Dockerfile'; Context = $src }
}

foreach ($service in $Services) {
    $spec = $images[$service] ?? (throw "Unknown service '$service'")
    $serviceTag = $Tag
    if (-not $serviceTag) {
        $valuesFile = Get-ChildItem $releases -Recurse -Filter "$service.yaml" | Select-Object -First 1
        if (-not $valuesFile) { throw "No release values file for '$service' under $releases" }
        $match = Select-String -Path $valuesFile.FullName -Pattern '^\s+tag:\s*"([^"]+)"' | Select-Object -First 1
        $serviceTag = $match.Matches[0].Groups[1].Value ?? (throw "No image tag in $($valuesFile.FullName)")
    }
    $image = "$($spec.Image):$serviceTag"

    Write-Host "==> $image"
    $buildArgs = @('-f', $spec.Dockerfile, '-t', $image)
    if ($spec.Project) { $buildArgs += @('--build-arg', "PROJECT=$($spec.Project)", '--build-arg', "DIR=$($spec.Dir)") }
    docker build @buildArgs $spec.Context
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $service" }

    kind load docker-image $image --name $Cluster
    if ($LASTEXITCODE -ne 0) { throw "kind load failed for $service" }
}
