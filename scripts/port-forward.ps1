#Requires -Version 7
# Port-forwards Prometheus, Loki and Alertmanager to localhost so the agent can run on the host
# (dotnet run --project src/SreAgent.Host, environment Development; see its appsettings.Development.json).
# Stops the forwards when you press Ctrl+C.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$forwards = @(
    @('svc/kube-prometheus-stack-prometheus', '19090:9090'),
    @('svc/loki', '13100:3100'),
    @('svc/kube-prometheus-stack-alertmanager', '19093:9093')
)
$procs = foreach ($f in $forwards) {
    Start-Process kubectl -ArgumentList 'port-forward', '-n', 'monitoring', $f[0], $f[1] -PassThru -WindowStyle Hidden
    Write-Host "forwarding $($f[0]) -> localhost:$(($f[1] -split ':')[0])"
}
try {
    Write-Host 'Ctrl+C to stop'
    while ($true) { Start-Sleep 5 }
} finally {
    $procs | Stop-Process -ErrorAction SilentlyContinue
}
