#Requires -Version 7
# Deletes the local kind cluster and everything in it (PVC data included).
[CmdletBinding()]
param(
    [string]$Name = 'sre-ai'
)
$ErrorActionPreference = 'Stop'

if ((kind get clusters) -contains $Name) {
    kind delete cluster --name $Name
} else {
    Write-Host "Cluster '$Name' does not exist."
}
