#!/bin/bash
# Restart the PennieBot service on the Azure VM
# Usage: ./scripts/bot-restart.sh

set -e

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
VM_NAME="${VM_NAME:-pennie-vm-prod}"

echo "Restarting PennieBot service on $VM_NAME..."

az vm run-command invoke \
  --resource-group "$RESOURCE_GROUP" \
  --name "$VM_NAME" \
  --command-id RunPowerShellScript \
  --scripts 'Restart-Service -Name PennieBot -Force; Start-Sleep -Seconds 5; Get-Service -Name PennieBot | Select-Object Status, Name'

echo ""
echo "Done. Use ./scripts/bot-logs.sh to check logs."
