#Requires -Version 7
# Builds the Phase 1 images (sandbox/<service>:0.1.0) from the repository state of the Phase 1 commit and loads them
# into kind. The current src/ is NOT used: since Phase 4 orders-api needs PostgreSQL, and the Phase 1 manifests have none.
# Tag 0.1.0 does not clash with the Helm releases (0.4.0).
[CmdletBinding()]
param([string]$Commit = '8a47ee6', [string]$Cluster = 'sre-ai')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$tmp = Join-Path ([IO.Path]::GetTempPath()) "sre-ai-phase1-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $tmp | Out-Null
try {
    git -C $repo archive $Commit src/Sandbox.Services | tar -x -C $tmp
    $root = Join-Path $tmp 'src' 'Sandbox.Services'
    foreach ($s in @(@('frontend', 'Frontend', 'Sandbox.Frontend'), @('orders-api', 'OrdersApi', 'Sandbox.OrdersApi'), @('worker', 'Worker', 'Sandbox.Worker'))) {
        $image = "sandbox/$($s[0]):0.1.0"
        Write-Host "==> $image"
        docker build -f (Join-Path $root 'Dockerfile') --build-arg "PROJECT=$($s[2])" -t $image (Join-Path $root $s[1])
        if ($LASTEXITCODE -ne 0) { throw "docker build failed: $image" }
        kind load docker-image $image --name $Cluster
        if ($LASTEXITCODE -ne 0) { throw "kind load failed: $image" }
    }
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
