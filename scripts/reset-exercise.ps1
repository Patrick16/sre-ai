#Requires -Version 7
# Put the cluster back into the state the phase docs describe, so an exercise makes sense again.
# Exercises can be done in any order: run this between them.
#
#   ./scripts/reset-exercise.ps1                  # Apps (default): ~2-3 min, keeps the data
#   ./scripts/reset-exercise.ps1 -Level Platform  # Apps + the platform layer (Traefik, Linkerd, monitoring values...)
#   ./scripts/reset-exercise.ps1 -Level Cluster -Yes   # cluster-down + up.ps1: ~9 min, everything from scratch, DATA IS LOST
#
# What each level does NOT do (on purpose): it never reverts your edits in git - it only warns. Throw them away
# yourself (`git restore <file>`) when the exercise was "change a values file".
[CmdletBinding()]
param(
    [ValidateSet('Apps', 'Platform', 'Cluster')][string]$Level = 'Apps',
    [switch]$Yes,            # required for Cluster: it deletes the cluster and the postgres data
    [switch]$SkipSmokeTest
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n=== $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

# Uncommitted changes to what defines the baseline: warn, never touch.
$dirty = git status --short -- infra src scripts
if ($dirty) {
    Write-Host "`nUncommitted changes in infra/src/scripts - the reset deploys WHAT IS ON DISK, not the baseline:" -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host "  $_" }
    Write-Host "  (undo with: git restore <file>; the exact pre-plan state is the tag phase-10-baseline)`n" -ForegroundColor Yellow
}

if ($Level -eq 'Cluster') {
    if (-not $Yes) {
        Write-Host 'Level Cluster deletes the kind cluster and ALL its data (postgres, Prometheus, agent reports), then rebuilds (~9 min).'
        Write-Host 'Run again with -Yes to confirm. Host files in .local/ are kept.'
        return
    }
    Step 'cluster-down' { ./scripts/cluster-down.ps1 }
    Step 'up.ps1' { ./scripts/up.ps1 -SkipSmokeTest:$SkipSmokeTest }
    return
}

Step 'kind nodes running' {
    # exercises that `docker stop` a node (docs/09, 10, 13)
    $stopped = docker ps -a --filter 'name=^sre-ai-' --filter 'status=exited' --format '{{.Names}}'
    foreach ($n in $stopped) { Write-Host "  starting $n"; docker start $n | Out-Null }
    if ($stopped) { kubectl wait --for=condition=Ready node --all --timeout=180s }
}

Step 'leftovers from exercises' {
    # Absent in the baseline, so deleting them is always "back to baseline".
    kubectl delete configmap sre-agent-kill-switch -n sre-agent --ignore-not-found          # docs/17: kill switch
    kubectl delete podchaos,networkchaos,stresschaos --all -n sandbox --ignore-not-found    # docs/18: Chaos Mesh experiments
    kubectl delete authorizationpolicies.policy.linkerd.io,servers.policy.linkerd.io --all -n sandbox --ignore-not-found  # docs/15
    kubectl delete pod -n sandbox -l 'reset-exercise=temp' --ignore-not-found               # label your throw-away pods with this
}

if ($Level -eq 'Platform') {
    Step 'platform (deploy-infra)' { ./scripts/deploy-infra.ps1 }
}

Step 'mesh recovery' { ./scripts/recover-mesh.ps1 }

# --force-conflicts: after `kubectl scale/edit` Helm would refuse (server-side apply conflict, docs/12 ex. 8).
# Replicas, fault modes (`faults:`), image tags and any values you changed come back from the values files.
Step 'apps (deploy-apps, force conflicts)' { ./scripts/deploy-apps.ps1 -ForceConflicts }

if (-not $SkipSmokeTest) {
    Step 'smoke test' { Start-Sleep 30; ./scripts/smoke-test.ps1 }
}
Write-Host "`nBack to baseline ($Level). Not restored by this level: postgres data (needs -Level Cluster), Alertmanager silences (expire in the UI), host files in .local/." -ForegroundColor Green
