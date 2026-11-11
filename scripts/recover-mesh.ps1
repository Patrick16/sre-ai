#Requires -Version 7
# After Docker Desktop / the host restarted, the kind nodes come back - but meshed pods often don't.
# Found in Phase 9 (docs/18, "cold start"):
#   1. on node restart kubelet recreates every pod sandbox; the linkerd-cni plugin asks the API server about the pod,
#      the API server isn't up yet ("dial tcp 10.96.0.1:443: connection refused") -> the pod's network has no
#      redirect into the proxy -> its init container linkerd-network-validator fails forever (status Unknown/Init);
#   2. while the Linkerd control plane is down, its injector webhook (failurePolicy Ignore) lets pods through
#      WITHOUT a proxy -> they run "1/1" outside the mesh, and default-deny NetworkPolicies (only 4143 between
#      meshed pods) make their calls time out.
#   3. (found 2026-11-11) the host SLEPT (laptop lid, hibernate) instead of restarting. Every proxy has a 24 h
#      certificate and renews it at 70 % of its life - on a timer that does not count the time the machine was asleep.
#      A night of sleep makes the timer fire after the certificate has expired, and a proxy with an expired
#      certificate can no longer renew it ("Failed to obtain identity ... broken pipe", every 10 s, forever): its pod
#      looks Running/Ready while every meshed connection to it fails. A healthy proxy never has less than ~7.2 h left,
#      so "less than -MinCertHours left" means expired or overdue.
# Nothing heals this by itself. Fix: recreate the stuck pods (control plane first), restart uninjected workloads,
# then restart the pods whose proxy certificate is expired or overdue.
# After a long sleep, renew everything at once: ./scripts/recover-mesh.ps1 -MinCertHours 25
[CmdletBinding()]
param(
    [string[]]$Namespaces = @('linkerd', 'linkerd-viz', 'sandbox', 'sre-agent'),
    [double]$MinCertHours = 7
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

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
# Proxies whose certificate is expired or whose renewal is overdue (case 3). The expiry time comes from the proxies'
# own metric in Prometheus; without Prometheus (or its CA file) this step is skipped with a warning.
$certs = $null
try {
    $expr = [uri]::EscapeDataString("(control_identity_cert_expiration_timestamp_seconds - time()) / 3600 < $MinCertHours")
    $certs = @((curl.exe -s --max-time 10 --ssl-no-revoke --cacert .local/sandbox-root-ca.crt "https://prometheus.localhost/api/v1/query?query=$expr" | ConvertFrom-Json).data.result)
}
catch { Write-Warning "proxy certificates not checked (Prometheus unreachable): $($_.Exception.Message)" }
foreach ($ns in $Namespaces) {
    $live = @(kubectl get pods -n $ns -o name) -replace '^pod/', ''
    $old = @($certs | Where-Object { $_.metric.namespace -eq $ns -and $_.metric.pod -in $live })   # Prometheus also remembers deleted pods for a few minutes
    if ($old.Count -eq 0) { continue }
    $text = ($old | ForEach-Object { '{0} ({1:N1} h left)' -f $_.metric.pod, [double]$_.value[1] }) -join ', '
    Write-Host "$ns : proxy certificate expired or overdue - recreating: $text"
    if ($ns -in 'sandbox', 'sre-agent') {
        # Rolling restart per workload: two frontend pods are replaced one after the other, not both at once.
        $pods = (kubectl get pods -n $ns -o json | ConvertFrom-Json).items | Where-Object { $_.metadata.name -in $old.metric.pod }
        foreach ($app in ($pods | ForEach-Object { $_.metadata.labels.'app.kubernetes.io/name' } | Sort-Object -Unique)) {
            $kind = if (kubectl get deployment $app -n $ns --ignore-not-found -o name) { 'deployment' } else { 'statefulset' }
            kubectl rollout restart "$kind/$app" -n $ns | Out-Null
            kubectl rollout status "$kind/$app" -n $ns --timeout=180s | Out-Null
        }
    }
    else {
        kubectl delete pod -n $ns @($old.metric.pod) --wait=false | Out-Null
        Start-Sleep 5   # let the Deployments create the replacements before waiting for "all Ready"
        kubectl wait -n $ns --for=condition=Ready pod --all --timeout=180s | Out-Null
    }
}
Write-Host 'done; check: kubectl get pods -A | Select-String -NotMatch "Running|Completed"'
