#!/bin/bash
# View PennieBot logs from the Azure VM
# Usage: ./scripts/bot-logs.sh [lines]
#   lines: Number of log lines to show (default: 50)

set -e

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
VM_NAME="${VM_NAME:-pennie-vm-prod}"
LINES="${1:-50}"

echo "Fetching last $LINES lines of PennieBot logs from $VM_NAME..."
echo ""

az vm run-command invoke \
  --resource-group "$RESOURCE_GROUP" \
  --name "$VM_NAME" \
  --command-id RunPowerShellScript \
  --scripts "
Write-Output '=== Service Status ==='
Get-Service -Name PennieBot | Select-Object Status, Name

Write-Output ''
Write-Output '=== Last $LINES lines of stdout log ==='
Get-Content 'C:\Pennie\logs\bot-stdout.log' -Tail $LINES -ErrorAction SilentlyContinue

Write-Output ''
Write-Output '=== Last 10 lines of stderr log ==='
Get-Content 'C:\Pennie\logs\bot-stderr.log' -Tail 10 -ErrorAction SilentlyContinue
" | jq -r '.value[0].message'
