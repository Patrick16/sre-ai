#Requires -Version 7
# Prints the generated Grafana admin password (stored only in the cluster, secret monitoring/grafana-admin).
$b64 = kubectl get secret grafana-admin -n monitoring -o jsonpath='{.data.admin-password}'
if ($LASTEXITCODE -ne 0) { throw 'secret monitoring/grafana-admin not found - run scripts/deploy-infra.ps1' }
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
