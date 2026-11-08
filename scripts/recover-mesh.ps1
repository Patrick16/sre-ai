#Requires -Version 7
# After Docker Desktop / the host restarted, the kind nodes come back - but meshed pods often don't.
# Found in Phase 9 (docs/18, "cold start"):
#   1. on node restart kubelet recreates every pod sandbox; the linkerd-cni plugin asks the API server about the pod,
#      the API server isn't up yet ("dial tcp 10.96.0.1:443: connection refused") -> the pod's network has no
#      redirect into the proxy -> its init container linkerd-network-validator fails forever (status Unknown/Init);
#   2. while the Linkerd control plane is down, its injector webhook (failurePolicy Ignore) lets pods through
#      WITHOUT a proxy -> they run "1/1" outside the mesh, and default-deny NetworkPolicies (only 4143 between
#      meshed pods) make their calls time out.
# Nothing heals this by itself. Fix: recreate the stuck pods (control plane first), then restart uninjected workloads.
[CmdletBinding()]
param([string[]]$Namespaces = @('linkerd', 'linkerd-viz', 'sandbox', 'sre-agent'))
$ErrorActionPreference = 'Stop'

function StuckPods([string]$ns) {
    $pods = (kubectl get pods -n $ns -o json | ConvertFrom-Json).items
    $pods | Where-Object {
        $validator = $_.status.initContainerStatuses | Where-Object name -eq 'linkerd-network-validator'
        $_.status.phase -eq 'Unknown' -or ($validator -and -not $validator.ready -and $validator.restartCount -gt 0)
    } | ForEach-Object { $_.metadata.name }
}

foreach ($ns in $Namespaces) {
    $stuck = @(StuckPods $ns)
    if ($stuck.Count -eq 0) { continue }
    Write-Host "$ns : recreating $($stuck.Count) stuck pod(s): $($stuck -join ', ')"
    kubectl delete pod -n $ns @stuck --wait=false | Out-Null
    if ($ns -eq 'linkerd') { kubectl wait -n linkerd --for=condition=Ready pod --all --timeout=180s | Out-Null }
}

# Workloads in meshed namespaces whose pods run without the proxy: a restart lets the (now running) injector add it.
foreach ($ns in $Namespaces | Where-Object { $_ -in 'sandbox', 'sre-agent' }) {
    $pods = (kubectl get pods -n $ns -o json | ConvertFrom-Json).items | Where-Object { $_.status.phase -eq 'Running' }
    $owners = $pods | Where-Object {
        -not (($_.spec.initContainers + $_.spec.containers) | Where-Object name -eq 'linkerd-proxy')
    } | ForEach-Object { $_.metadata.labels.'app.kubernetes.io/name' } | Sort-Object -Unique
    foreach ($app in $owners) {
        $kind = if (kubectl get deployment $app -n $ns --ignore-not-found -o name) { 'deployment' } else { 'statefulset' }
        Write-Host "$ns : $kind/$app runs outside the mesh - restarting"
        kubectl rollout restart "$kind/$app" -n $ns | Out-Null
    }
}
Write-Host 'done; check: kubectl get pods -A | Select-String -NotMatch "Running|Completed"'
