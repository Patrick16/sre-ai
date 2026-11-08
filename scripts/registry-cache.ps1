#Requires -Version 7
# Pull-through image caches for the kind cluster (idempotent; scripts/cluster-up.ps1 calls it).
#
# Every kind node has its own containerd and image store and can't see Docker Desktop's images, so without
# a cache each node downloads every community image itself, again after every cluster re-creation.
# Here: one registry:3 container per upstream registry, in proxy mode, data in a named Docker volume.
# Nodes are told (hosts.toml) to pull through it; if a cache is down they fall back to the internet.
# The caches are deliberately NOT removed by cluster-down.ps1 - surviving the cluster is the point.
[CmdletBinding()]
param(
    [string]$Cluster = 'sre-ai',
    # Remove the cache containers (add -Purge to also delete the cached data).
    [switch]$Remove,
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'

# registry:2, not 3: distribution v3's proxy answers a blob only after fetching it whole, so layers that take
# >30s to download fail (HTTP 500, "context canceled") and containerd silently falls back to the internet.
# v2 streams the blob to the client while caching it.
$image  = 'registry:2.8.3'
$caches = [ordered]@{
    'docker.io'       = 'https://registry-1.docker.io'
    'quay.io'         = 'https://quay.io'
    'registry.k8s.io' = 'https://registry.k8s.io'
    'ghcr.io'         = 'https://ghcr.io'          # Phase 9: Chaos Mesh images
}
function Get-CacheName([string]$Registry) { 'kind-cache-' + ($Registry -replace '\.', '-') }

if ($Remove) {
    foreach ($registry in $caches.Keys) {
        $name = Get-CacheName $registry
        docker rm -f $name 2>$null | Out-Null
        if ($Purge) { docker volume rm $name 2>$null | Out-Null }
        Write-Host "removed $name$(if ($Purge) { ' (and its data)' })"
    }
    return
}

# kind creates the "kind" network with the first cluster; make sure it exists for the caches to join.
docker network inspect kind *> $null
if ($LASTEXITCODE -ne 0) { docker network create kind | Out-Null }

foreach ($registry in $caches.Keys) {
    $name = Get-CacheName $registry
    $state = docker inspect -f '{{.State.Running}} {{.Config.Image}}' $name 2>$null
    if ($state -eq "true $image") { continue }
    if ($state) { docker rm -f $name | Out-Null }   # stopped, broken or other image: recreate (data is in the volume)

    docker run -d --restart=always --name $name --network kind `
        -v "${name}:/var/lib/registry" `
        -e "REGISTRY_PROXY_REMOTEURL=$($caches[$registry])" `
        -e "REGISTRY_LOG_LEVEL=info" `
        $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "starting cache $name failed" }
    Write-Host "cache $name -> $($caches[$registry])"
}

$nodes = kind get nodes --name $Cluster
if (-not $nodes) { Write-Host "cluster '$Cluster' not found: caches are running, nodes will be configured by cluster-up.ps1"; return }

# containerd re-reads hosts.toml on every pull: no restart needed.
foreach ($node in $nodes) {
    foreach ($registry in $caches.Keys) {
        $dir = "/etc/containerd/certs.d/$registry"
        $hostsToml = @"
server = "$($caches[$registry])"

[host."http://$(Get-CacheName $registry):5000"]
  capabilities = ["pull", "resolve"]
"@
        docker exec $node mkdir -p $dir
        $hostsToml | docker exec -i $node cp /dev/stdin "$dir/hosts.toml"
        if ($LASTEXITCODE -ne 0) { throw "configuring $registry mirror on $node failed" }
    }
}
Write-Host "nodes configured: $($nodes -join ', ')"
