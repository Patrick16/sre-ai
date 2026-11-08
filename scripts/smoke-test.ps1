#Requires -Version 7
# "Is everything actually working?" - not "did the deploy scripts exit 0". Phase 9 showed the difference: after a
# Docker Desktop restart every script was happy while meshed pods ran outside the mesh and the site answered 500.
# Read-only; exit code 1 if any check fails. Run after up.ps1, after a restart, or whenever in doubt.
[CmdletBinding()]
param([int]$AgentPort = 18094)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$name, [scriptblock]$test) {
    try {
        $detail = & $test
        $ok = $detail -isnot [string] -or -not $detail.StartsWith('FAIL')
        $results.Add([pscustomobject]@{ Ok = $ok; Check = $name; Detail = ("$detail" -replace '^FAIL:?\s*', '') })
    }
    catch { $results.Add([pscustomobject]@{ Ok = $false; Check = $name; Detail = $_.Exception.Message }) }
}
function Curl([string]$url) { curl.exe -s -o NUL -w '%{http_code}' --max-time 10 --ssl-no-revoke --cacert .local/sandbox-root-ca.crt $url }
function Prom([string]$expr) {
    $r = curl.exe -s --max-time 10 --ssl-no-revoke --cacert .local/sandbox-root-ca.crt "https://prometheus.localhost/api/v1/query?query=$([uri]::EscapeDataString($expr))" | ConvertFrom-Json
    @($r.data.result)
}

Check 'kubectl context' { $c = kubectl config current-context; if ($c -ne 'kind-sre-ai') { "FAIL: $c" } else { $c } }
Check 'nodes Ready' {
    $nodes = (kubectl get nodes -o json | ConvertFrom-Json).items
    $bad = $nodes | ? { ($_.status.conditions | ? type -eq 'Ready').status -ne 'True' }
    if ($bad) { "FAIL: not ready: $($bad.metadata.name -join ', ')" } else { "$($nodes.Count) nodes" }
}

$namespaces = 'kube-system', 'monitoring', 'traefik', 'cert-manager', 'linkerd', 'linkerd-cni', 'linkerd-viz', 'chaos-mesh', 'sandbox', 'sre-agent'
foreach ($ns in $namespaces) {
    Check "pods ready: $ns" {
        $pods = (kubectl get pods -n $ns -o json | ConvertFrom-Json).items | ? { $_.status.phase -ne 'Succeeded' }
        $bad = $pods | ? { ($_.status.conditions | ? type -eq 'Ready').status -ne 'True' }
        if (-not $pods) { 'FAIL: no pods' } elseif ($bad) { "FAIL: $($bad.metadata.name -join ', ')" } else { "$(@($pods).Count) pods" }
    }
}
foreach ($ns in 'sandbox', 'sre-agent') {
    Check "every pod meshed: $ns" {
        $pods = (kubectl get pods -n $ns -o json | ConvertFrom-Json).items | ? { $_.status.phase -eq 'Running' }
        $bare = $pods | ? { -not (($_.spec.initContainers + $_.spec.containers) | ? name -eq 'linkerd-proxy') }
        if ($bare) { "FAIL: no proxy (run scripts/recover-mesh.ps1): $($bare.metadata.name -join ', ')" } else { 'linkerd-proxy in every pod' }
    }
}

Check 'site https://sandbox.localhost/' { $c = Curl 'https://sandbox.localhost/'; if ($c -ne '200') { "FAIL: HTTP $c" } else { 'HTTP 200 (frontend -> orders-api -> postgres)' } }
Check 'api https://sandbox.localhost/api/info' { $c = Curl 'https://sandbox.localhost/api/info'; if ($c -ne '200') { "FAIL: HTTP $c" } else { 'HTTP 200' } }
Check 'grafana' { $c = Curl 'https://grafana.localhost/api/health'; if ($c -ne '200') { "FAIL: HTTP $c" } else { 'HTTP 200' } }
Check 'alertmanager' { $c = Curl 'https://alertmanager.localhost/-/ready'; if ($c -ne '200') { "FAIL: HTTP $c" } else { 'HTTP 200' } }
Check 'scrape targets in sandbox up' {
    $down = Prom 'up{namespace=~"sandbox|sre-agent"} == 0'
    $all = Prom 'count(up{namespace=~"sandbox|sre-agent"})'
    if (-not $all) { 'FAIL: no targets' } elseif ($down) { "FAIL: down: $(($down | % { $_.metric.pod }) -join ', ')" } else { "$($all[0].value[1]) targets up" }
}
Check 'SLI recording rules' {
    $s = Prom 'slo:sli_error:ratio_rate5m'
    if (-not $s) { 'FAIL: no slo:sli_error:ratio_rate5m series (traffic? rules?)' }
    else { ($s | % { '{0} {1:P1}' -f $_.metric.slo, [double]$_.value[1] }) -join ', ' }
}
Check 'logs in Loki' {
    $q = [uri]::EscapeDataString('{namespace="sandbox"}')
    $pf = Start-Process kubectl -ArgumentList 'port-forward', '-n', 'monitoring', 'svc/loki', '13100:3100' -PassThru -WindowStyle Hidden
    try {
        Start-Sleep 3
        $start = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds() * 1000000
        $r = curl.exe -s --max-time 10 "http://localhost:13100/loki/api/v1/query_range?query=$q&limit=5&start=$start" | ConvertFrom-Json
        $n = @($r.data.result).Count
        if ($n -eq 0) { 'FAIL: no log streams from sandbox in 10 min' } else { "$n streams" }
    }
    finally { Stop-Process -Id $pf.Id -ErrorAction SilentlyContinue }
}
Check 'admission policies for the agent' {
    $p = kubectl get validatingadmissionpolicybindings -o name
    $need = 'sre-agent-restart-only', 'sre-agent-delete-deployment-pods-only'
    $missing = $need | ? { "validatingadmissionpolicybinding.admissionregistration.k8s.io/$_" -notin $p }
    if ($missing) { "FAIL: missing $($missing -join ', ')" } else { 'both bound' }
}

$pf = Start-Process kubectl -ArgumentList 'port-forward', '-n', 'sre-agent', 'svc/sre-agent', "$($AgentPort):80" -PassThru -WindowStyle Hidden
try {
    Start-Sleep 3
    Check 'agent ready' { $c = curl.exe -s -o NUL -w '%{http_code}' --max-time 5 "http://localhost:$AgentPort/health/ready"; if ($c -ne '200') { "FAIL: HTTP $c" } else { 'HTTP 200' } }
    Check 'agent actions mode' {
        $a = curl.exe -s --max-time 5 "http://localhost:$AgentPort/actions" | ConvertFrom-Json
        if ($a.mode -ne 'DryRun') { "FAIL: mode is '$($a.mode)' (values say DryRun - a game day left it raised?)" } else { "DryRun; allowed: $(($a.allowed.PSObject.Properties | % { "$($_.Name)=$($_.Value)" }) -join ', ')" }
    }
}
finally { Stop-Process -Id $pf.Id -ErrorAction SilentlyContinue }

# Not part of the cluster, so a warning, not a failure: without it the agent can't think, everything else works.
$llm = try { (curl.exe -s --max-time 3 http://localhost:1234/v1/models | ConvertFrom-Json).data.id } catch { $null }
$llmText = if ($llm -contains 'qwen/qwen3.5-9b') { 'qwen/qwen3.5-9b available' } elseif ($llm) { "WARN: models: $($llm -join ', '); load qwen/qwen3.5-9b (see CLAUDE.md)" } else { 'WARN: LM Studio not reachable on :1234 - agent runs will fail until it is started' }

$results | % { '{0} {1,-38} {2}' -f ($_.Ok ? '✅' : '❌'), $_.Check, $_.Detail }
"   {0,-38} {1}" -f 'LLM (LM Studio on the host)', $llmText
$failed = @($results | ? { -not $_.Ok }).Count
if ($failed) { Write-Host "`n$failed check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nall $($results.Count) checks passed" -ForegroundColor Green
