#Requires -Version 7
# Builds the sandbox service images and loads them into the kind cluster's nodes (no registry needed).
[CmdletBinding()]
param(
    [string]$Tag = '0.1.0',
    [string]$Cluster = 'sre-ai',
    [string[]]$Services = @('frontend', 'orders-api', 'worker')
)
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..' 'src' 'Sandbox.Services'
$projects = @{
    'frontend'   = @{ Dir = 'Frontend';  Project = 'Sandbox.Frontend' }
    'orders-api' = @{ Dir = 'OrdersApi'; Project = 'Sandbox.OrdersApi' }
    'worker'     = @{ Dir = 'Worker';    Project = 'Sandbox.Worker' }
}

foreach ($service in $Services) {
    $p = $projects[$service] ?? (throw "Unknown service '$service'")
    $image = "sandbox/${service}:$Tag"

    Write-Host "==> $image"
    docker build -f (Join-Path $root 'Dockerfile') --build-arg "PROJECT=$($p.Project)" -t $image (Join-Path $root $p.Dir)
    if ($LASTEXITCODE -ne 0) { throw "docker build failed for $service" }

    kind load docker-image $image --name $Cluster
    if ($LASTEXITCODE -ne 0) { throw "kind load failed for $service" }
}
