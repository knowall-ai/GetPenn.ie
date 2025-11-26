#!/bin/bash
# Check PennieBot health status and endpoints
# Usage: ./scripts/bot-health.sh

set -e

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
VM_NAME="${VM_NAME:-pennie-vm-prod}"

echo "Checking PennieBot health..."
echo ""

# Get VM public IP
VM_IP=$(az vm show -g "$RESOURCE_GROUP" -n "$VM_NAME" -d --query publicIps -o tsv 2>/dev/null)

if [ -z "$VM_IP" ]; then
    echo "Error: Could not get VM IP address"
    exit 1
fi

echo "VM IP: $VM_IP"
echo ""

# Check health endpoint
echo "Health endpoint (/health):"
HEALTH=$(curl -sk "https://$VM_IP/health" 2>/dev/null)
if [ "$HEALTH" = "Healthy" ]; then
    echo "  ✅ $HEALTH"
else
    echo "  ❌ Response: $HEALTH"
fi
echo ""

# Check root endpoint
echo "Root endpoint (/):"
ROOT=$(curl -sk "https://$VM_IP/" 2>/dev/null)
if echo "$ROOT" | grep -q "Pennie the Prepper Bot"; then
    echo "  ✅ Bot responding"
    echo "$ROOT" | jq -r '  "  Name: \(.name)\n  Status: \(.status)\n  Version: \(.version)"' 2>/dev/null || echo "  $ROOT"
else
    echo "  ❌ Unexpected response: $ROOT"
fi
echo ""

# Check backend connectivity
echo "Backend connectivity:"
BACKEND=$(curl -s "https://pennie-backend-prod.azurewebsites.net/api/read_projects" 2>/dev/null)
if echo "$BACKEND" | grep -q '"success":true'; then
    COUNT=$(echo "$BACKEND" | jq -r '.count' 2>/dev/null)
    echo "  ✅ Backend reachable ($COUNT projects)"
else
    echo "  ❌ Backend error: $BACKEND"
fi
