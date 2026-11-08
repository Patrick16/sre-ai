#Requires -Version 7
# Everything from nothing (Phase 10): one command from an empty Docker to a working, checked sandbox.
#
#   ./scripts/up.ps1               # idempotent: on an existing cluster it re-applies everything (safe)
#   ./scripts/up.ps1 -SkipBuild    # images already loaded (e.g. right after a cluster-down + up with the same tags)
#
# The steps are the same scripts the phases introduced - this file only orders them and checks the result:
#   prerequisites -> cluster-up (kind + image caches) -> deploy-infra (platform) -> build-images -> deploy-apps -> smoke-test
# Secrets (API key, DB password, Grafana) are generated on the first run and never stored in git.
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipSmokeTest
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$timings = [ordered]@{}

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $body
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
    $timings[$name] = $sw.Elapsed
}

Step 'prerequisites' {
    # Version floors = what this repo was built with (docs/09). Older tools mostly work, but not all flags exist.
    $need = [ordered]@{
        docker  = { (docker version --format '{{.Client.Version}}') }
        kind    = { ((kind version) -split ' ')[1].TrimStart('v') }
        kubectl = { ((kubectl version --client -o json | ConvertFrom-Json).clientVersion.gitVersion).TrimStart('v') }
        helm    = { (helm version --template '{{.Version}}').TrimStart('v') }
        dotnet  = { (dotnet --version) }
    }
    foreach ($tool in $need.Keys) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' not found in PATH - see docs/09-dev-environment.md" }
        Write-Host ("  {0,-8} {1}" -f $tool, (& $need[$tool]))
    }
    if (-not ((helm version --template '{{.Version}}') -match '^v4\.')) { throw 'Helm 4 is required (server-side apply, --rollback-on-failure)' }
    docker info *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is not running - start Docker Desktop' }
    $global:LASTEXITCODE = 0
}

Step 'cluster (kind + image caches)' { ./scripts/cluster-up.ps1 }
Step 'platform (deploy-infra)' { ./scripts/deploy-infra.ps1 }
if (-not $SkipBuild) { Step 'images (build + kind load)' { ./scripts/build-images.ps1 } }
Step 'apps (deploy-apps)' { ./scripts/deploy-apps.ps1 }
if (-not $SkipSmokeTest) {
    # Fresh pods need a few scrapes before SLIs and logs exist.
    Step 'smoke test' { Start-Sleep 60; ./scripts/smoke-test.ps1 }
}

Write-Host "`n=== done" -ForegroundColor Green
$total = [TimeSpan]::Zero
foreach ($k in $timings.Keys) { Write-Host ("  {0,-30} {1:mm\:ss}" -f $k, $timings[$k]); $total += $timings[$k] }
Write-Host ("  {0,-30} {1:hh\:mm\:ss}" -f 'total', $total)
Write-Host @'

  App           https://sandbox.localhost
  Grafana       https://grafana.localhost   (admin / ./scripts/grafana-password.ps1)
  Prometheus    https://prometheus.localhost
  Agent API     kubectl port-forward -n sre-agent svc/sre-agent 8099:80
  Trust the CA  curl.exe --ssl-no-revoke --cacert .local/sandbox-root-ca.crt ...  (or import it yourself - your call)
  The agent needs LM Studio on the host with qwen/qwen3.5-9b (--context-length 32768 --gpu max).
'@
