#Requires -Version 7
# Game day (Phase 9, docs/18): runs chaos scenarios one after another and scores the agent.
#
# Per scenario (infra/chaos/scenarios.json):
#   1. steady state first   - every 5m SLI <= 1%, all pods ready, no restarts, for -QuietMinutes (the agent's
#                             look-back windows must not reach the previous scenario);
#   2. inject the fault     - a Chaos Mesh experiment or a service "bad mode" (helm --set-json faults.*);
#   3. declare an incident  - POST /runs {"incident": true, "note": ...} once users would notice (SLI > 1%);
#   4. end the fault        - after faultMinutes, so that recovery (and the agent's completion check) is possible;
#   5. wait for the run, fetch its report, score it against the scenario's expectations.
# Output: .local/game-day/<time>/scorecard.md + one report per scenario. Gitignored: results go to docs/18 by hand.
#
#   ./scripts/game-day.ps1                                   # all scenarios, agent in its configured mode (DryRun)
#   ./scripts/game-day.ps1 -Scenarios error-rate-orders-api -AgentMode Auto
[CmdletBinding()]
param(
    [string[]]$Scenarios,
    [ValidateSet('', 'DryRun', 'Approve', 'Auto')] [string]$AgentMode = '',
    [int]$AgentPort = 18096,
    # Calm minutes before each scenario. The agent looks back 10-30 min (logs, events): without a quiet period the
    # first game day blamed a CPU-stress "incident" on the OOMKills left over from the previous scenario.
    [int]$QuietMinutes = 15
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
$chart = 'infra/helm/sandbox-service'
$all = (Get-Content 'infra/chaos/scenarios.json' -Raw | ConvertFrom-Json).scenarios
$selected = if ($Scenarios) { $all | ? { $_.name -in $Scenarios } } else { $all }
if (-not $selected) { throw "no such scenario; known: $($all.name -join ', ')" }
$out = Join-Path $root '.local' 'game-day' (Get-Date -Format 'yyyyMMdd-HHmm')
New-Item -ItemType Directory -Force $out | Out-Null
$t0 = Get-Date
function Log($m) { $line = '[{0:hh\:mm\:ss}] {1}' -f ((Get-Date) - $t0), $m; Write-Host $line; Add-Content (Join-Path $out 'game-day.log') $line }

function Prom([string]$expr) {
    $r = curl.exe -s --ssl-no-revoke --cacert .local/sandbox-root-ca.crt "https://prometheus.localhost/api/v1/query?query=$([uri]::EscapeDataString($expr))" | ConvertFrom-Json
    @($r.data.result)
}
function MaxSli { $v = Prom 'max(slo:sli_error:ratio_rate5m{namespace="sandbox"})'; if ($v) { [double]$v[0].value[1] } else { 1.0 } }
function NotReadyPods {
    @(Prom 'sum by (pod) (kube_pod_status_ready{namespace="sandbox",condition="false"} * on (namespace, pod) group_left () (kube_pod_status_phase{namespace="sandbox",phase=~"Running|Pending"} == 1)) > 0').Count
}
function Restarts { @(Prom 'sum by (pod) (increase(kube_pod_container_status_restarts_total{namespace="sandbox"}[5m])) > 0').Count }
function Steady { (MaxSli) -le 0.01 -and (NotReadyPods) -eq 0 -and (Restarts) -eq 0 }
# Steady state that has LASTED $quiet minutes (checked every 20 s), within $minutes overall.
function WaitSteady([int]$minutes, [int]$quiet) {
    $until = (Get-Date).AddMinutes($minutes); $calmSince = $null
    while ((Get-Date) -lt $until) {
        if (Steady) { $calmSince ??= Get-Date; if (((Get-Date) - $calmSince).TotalMinutes -ge $quiet) { return $true } }
        else { $calmSince = $null }
        Start-Sleep 20
    }
    return $false
}
function Api([string]$method, [string]$path, $body) {
    $curlArgs = @('-s', '-X', $method, "http://localhost:$AgentPort$path")
    if ($body) { $curlArgs += @('-H', 'Content-Type: application/json', '-d', ($body | ConvertTo-Json -Compress)) }
    # Only needed when the agent runs with Agent__ApiToken (docs/17): $env:SRE_AGENT_TOKEN = '<the token>'
    if ($env:SRE_AGENT_TOKEN) { $curlArgs += @('-H', "X-Agent-Token: $env:SRE_AGENT_TOKEN") }
    curl.exe @curlArgs
}

function ActiveRun { @(Api GET '/runs' | ConvertFrom-Json) | ? { $_.status -in 'queued', 'running' } | Select-Object -First 1 -ExpandProperty id }

function Inject($s) {
    if ($s.inject.kind -eq 'chaos') { kubectl apply -f "infra/chaos/$($s.inject.file)" | Out-Null; return }
    # Invariant culture: in a Russian locale "$(0.3)" would be "0,3", which isn't JSON.
    $json = ($s.inject.values.PSObject.Properties | % { "faults.$($_.Name)=" + [string]::Format([cultureinfo]::InvariantCulture, '{0}', $_.Value) })
    # A FLAT list of strings ('--set-json', 'faults.x=1', '--set-json', ...): % unrolls each pair, so splatting passes every
    # element to helm as its own argument. (Do not "optimise" this into an array of arrays - that would glue the pair into one.)
    [string[]]$setArgs = $json | % { '--set-json'; $_ }
    helm upgrade $s.inject.release $chart -n sandbox -f "infra/helm/releases/sandbox/$($s.inject.release).yaml" @setArgs --wait | Out-Null
}
function EndFault($s) {
    if ($s.inject.kind -eq 'chaos') { kubectl delete -f "infra/chaos/$($s.inject.file)" --ignore-not-found | Out-Null; return }
    ./scripts/deploy-apps.ps1 -Services $s.inject.release | Out-Null   # back to the values file: faults off
    # Experiment hygiene: a leak leaves "restarts 2 (last: OOMKilled)" on the pods for as long as they live, and the
    # next scenario's agent blamed memory for a database slowdown 2.5 hours later. Fresh pods = a clean slate.
    kubectl rollout restart "deployment/$($s.inject.release)" -n sandbox | Out-Null
    kubectl rollout status "deployment/$($s.inject.release)" -n sandbox --timeout=300s | Out-Null
}

$pf = Start-Process kubectl -ArgumentList 'port-forward', '-n', 'sre-agent', 'svc/sre-agent', "$($AgentPort):80" -PassThru -WindowStyle Hidden
Start-Sleep 3
if ($AgentMode) {
    Log "agent: actions ceiling -> $AgentMode for the game day"
    helm upgrade sre-agent $chart -n sre-agent -f infra/helm/releases/sre-agent/sre-agent.yaml --set "configEnv.Actions__Mode=$AgentMode" --wait | Out-Null
    Stop-Process -Id $pf.Id; Start-Sleep 5
    $pf = Start-Process kubectl -ArgumentList 'port-forward', '-n', 'sre-agent', 'svc/sre-agent', "$($AgentPort):80" -PassThru -WindowStyle Hidden
    Start-Sleep 3
}
$rows = @()
try {
    foreach ($s in $selected) {
        Log "=== $($s.name): $($s.hypothesis)"
        if (-not (WaitSteady ($QuietMinutes + 30) $QuietMinutes)) { Log 'no lasting steady state - skipped'; $rows += [pscustomobject]@{ Name = $s.name; Verdict = 'skipped: no steady state' }; continue }

        # One run per namespace: a lagging burn-rate alert from the previous scenario may have started one.
        $busyUntil = (Get-Date).AddMinutes(30)
        while ((ActiveRun) -and (Get-Date) -lt $busyUntil) { Start-Sleep 20 }
        if (ActiveRun) { Log 'the agent is still busy with another run - skipped'; $rows += [pscustomobject]@{ Name = $s.name; Verdict = 'skipped: agent busy' }; continue }

        Inject $s; $injectedAt = Get-Date
        Log "injected; fault ends in $($s.faultMinutes) min"

        if (-not $s.agent) {
            # Infrastructure hypothesis: watch the SLIs for the fault window.
            $worst = 0.0
            while (((Get-Date) - $injectedAt).TotalMinutes -lt $s.faultMinutes) { $worst = [math]::Max($worst, (MaxSli)); Start-Sleep 20 }
            EndFault $s
            $held = $worst -le $s.maxSli
            Log ("worst 5m SLI during the experiment: {0:P1} -> hypothesis {1}" -f $worst, ($held ? 'HELD' : 'REFUTED'))
            $rows += [pscustomobject]@{ Name = $s.name; Verdict = ($held ? 'hypothesis held' : 'hypothesis refuted'); RootCause = ("worst SLI {0:P1}" -f $worst) }
            continue
        }

        # Declare the incident once users would notice: SLI over budget, or pods restarting/not ready.
        while (((Get-Date) - $injectedAt).TotalMinutes -lt 4 -and (MaxSli) -le 0.01 -and (NotReadyPods) -eq 0) { Start-Sleep 15 }
        $run = (Api POST '/runs' @{ incident = $true; note = $s.note } | ConvertFrom-Json).runId
        if (-not $run) {
            # 409: an alert already started a run for this namespace - it is investigating the same fault, adopt it.
            $run = ActiveRun
            Log "an alert-triggered run is already investigating: adopting $run"
        }
        if (-not $run) { EndFault $s; Log 'could not start or find a run - skipped'; $rows += [pscustomobject]@{ Name = $s.name; Verdict = 'skipped: no run' }; continue }
        Log "incident declared ($('{0:N1}' -f ((Get-Date) - $injectedAt).TotalMinutes) min after injection): run $run"

        $ended = $false; $status = $null
        while (((Get-Date) - $injectedAt).TotalMinutes -lt 60) {
            Start-Sleep 20
            if (-not $ended -and ((Get-Date) - $injectedAt).TotalMinutes -ge $s.faultMinutes) { EndFault $s; $ended = $true; Log 'fault ended' }
            $status = Api GET "/runs/$run" | ConvertFrom-Json
            if ($status.status -eq 'finished') { break }
        }
        if (-not $ended) { EndFault $s; Log 'fault ended' }

        $report = Api GET "/runs/$run/report"
        $report | Set-Content -Encoding utf8 (Join-Path $out "$($s.name).md")
        $actions = ($report -split "`n" | ? { $_ -like '- Actions:*' } | Select-Object -First 1) -replace '^- Actions:\s*', ''
        $cause = "$($status.hypothesis)"
        $checks = @()
        foreach ($rx in @($s.expect.rootCause)) { if ($rx) { $checks += ($cause -match "(?i)$rx") } }
        if ($s.expect.notRootCause) { $checks += -not ($cause -match "(?i)$($s.expect.notRootCause)") }
        if ($s.expect.noAction) { $checks += ($actions -like 'none*') }
        $score = "$(@($checks | ? { $_ }).Count)/$($checks.Count)"
        Log "run $($status.outcome) after $($status.iterations) iterations; score $score; actions: $actions"
        $rows += [pscustomobject]@{ Name = $s.name; Verdict = "$($status.outcome), score $score"; Iterations = $status.iterations
            Minutes = [math]::Round(((Get-Date) - $injectedAt).TotalMinutes, 1); Actions = $actions; RootCause = $cause }
    }
}
finally {
    foreach ($s in $selected) { if ($s.inject.kind -eq 'chaos') { kubectl delete -f "infra/chaos/$($s.inject.file)" --ignore-not-found 2>$null | Out-Null } }
    if ($AgentMode) { ./scripts/deploy-apps.ps1 -Services sre-agent | Out-Null; Log 'agent: back to the mode in its values file' }
    Stop-Process -Id $pf.Id -ErrorAction SilentlyContinue
}

$md = @("# Game day $(Get-Date -Format 'yyyy-MM-dd HH:mm')", '', "Agent mode: $(if ($AgentMode) { $AgentMode } else { 'from values' })", '',
    '| Scenario | Verdict | Iterations | Min | Actions | Root cause (synthesis) |', '|---|---|---|---|---|---|')
$md += $rows | % { "| $($_.Name) | $($_.Verdict) | $($_.Iterations) | $($_.Minutes) | $(($_.Actions -replace '\|', '/')) | $(($_.RootCause -replace '\|', '/' -replace "`n", ' ')) |" }
$md | Set-Content -Encoding utf8 (Join-Path $out 'scorecard.md')
Log "scorecard: $(Join-Path $out 'scorecard.md')"
Log 'GAME DAY DONE'
