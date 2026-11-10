#Requires -Version 7
# The sre-agent NetworkPolicy (infra/helm/releases/sre-agent/sre-agent.yaml) contains two addresses that depend on
# your Docker setup, not on this repo:
#   - the kind node network (where the kube-apiserver lives)  -> default 172.18.0.0/16
#   - host.docker.internal as seen from inside the cluster     -> default 192.168.65.254 (LM Studio runs on the host)
# If they differ, the agent silently loses the Kubernetes API or the LLM (every call just times out). This script
# reads the real values and tells you whether the values file still matches. Read-only; exit code 1 on a mismatch.
[CmdletBinding()]
param([string]$ClusterName = 'sre-ai')
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$values = Get-Content infra/helm/releases/sre-agent/sre-agent.yaml -Raw
$configuredCidr = [regex]::Match($values, 'cidr:\s*(\d+\.\d+\.\d+\.\d+/\d+)\s*,\s*port:\s*6443').Groups[1].Value
$configuredHost = [regex]::Match($values, 'cidr:\s*(\d+\.\d+\.\d+\.\d+)/32\s*,\s*port:\s*1234').Groups[1].Value

# The IPv4 subnet of the docker network "kind" (it can also have an IPv6 one).
$subnets = (docker network inspect kind -f '{{range .IPAM.Config}}{{.Subnet}} {{end}}') -split '\s+' | ? { $_ -match '^\d+\.\d+\.\d+\.\d+/\d+$' }
# host.docker.internal as the cluster's nodes resolve it (not as Windows does).
$hostIp = (docker exec "$ClusterName-control-plane" sh -c 'getent hosts host.docker.internal | cut -d" " -f1').Trim()

$bad = $false
if ($subnets -contains $configuredCidr) { Write-Host "OK   kind network: $configuredCidr" }
else { Write-Host "DIFF kind network is $($subnets -join ', '), the values file says $configuredCidr -> change allowToCidrs (port 6443)"; $bad = $true }
if ($hostIp -eq $configuredHost) { Write-Host "OK   host.docker.internal: $hostIp" }
else { Write-Host "DIFF host.docker.internal is $hostIp, the values file says $configuredHost -> change allowToCidrs (port 1234)"; $bad = $true }
if ($bad) { Write-Host 'Then: ./scripts/deploy-apps.ps1 -Services sre-agent'; exit 1 }
