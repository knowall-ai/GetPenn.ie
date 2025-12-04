#!/bin/bash
# Test bot endpoint connectivity (SSL, health, messaging endpoint)
# Usage: ./tests/bot-endpoint-test.sh [environment]
#   environment: prod (default) or test

set -e

ENV="${1:-prod}"

if [ "$ENV" = "test" ]; then
    BOT_URL="https://pennie-test-vgn7kzlubtavo.uksouth.cloudapp.azure.com"
    RG_NAME="TMinus15Agents-Test"
    VM_NAME="pennie-vm-test"
else
    BOT_URL="https://pennie-prod-mmdxqm3w7kjwm.uksouth.cloudapp.azure.com"
    RG_NAME="TMinus15Agents"
    VM_NAME="pennie-vm-prod"
fi

echo "Bot Endpoint Connectivity Test"
echo "Environment: $ENV"
echo "Bot URL: $BOT_URL"
echo "=============================================="
echo ""

FAILED=0

# Test 1: DNS Resolution
echo "Test 1: DNS Resolution"
HOST=$(echo "$BOT_URL" | sed 's|https://||')
IP=$(nslookup "$HOST" 2>/dev/null | grep -A1 "Name:" | grep "Address" | head -1 | awk '{print $2}')
if [ -n "$IP" ]; then
    echo "  OK: $HOST resolves to $IP"
else
    echo "  FAIL: Could not resolve $HOST"
    FAILED=1
fi
echo ""

# Test 2: SSL/TLS Connection
echo "Test 2: SSL/TLS Connection"
SSL_INFO=$(curl -s -k -v "$BOT_URL" 2>&1 | grep -E "SSL connection|subject:" | head -2)
if echo "$SSL_INFO" | grep -q "SSL connection"; then
    echo "  OK: SSL connection established"
    CERT_CN=$(echo "$SSL_INFO" | grep "subject:" | sed 's/.*CN=//' | cut -d',' -f1)
    echo "  Certificate CN: $CERT_CN"
else
    echo "  FAIL: SSL connection failed"
    FAILED=1
fi
echo ""

# Test 3: Health Endpoint
echo "Test 3: Health Endpoint"
HEALTH_RESPONSE=$(curl -s -k "$BOT_URL/health" 2>&1)
if [ "$HEALTH_RESPONSE" = "Healthy" ]; then
    echo "  OK: Health check returned 'Healthy'"
else
    echo "  FAIL: Health check returned '$HEALTH_RESPONSE'"
    FAILED=1
fi
echo ""

# Test 4: Root Endpoint (bot info)
echo "Test 4: Root Endpoint"
ROOT_RESPONSE=$(curl -s -k "$BOT_URL/" 2>&1)
if echo "$ROOT_RESPONSE" | grep -q "Pennie"; then
    echo "  OK: Root endpoint responded with bot info"
    STATUS=$(echo "$ROOT_RESPONSE" | jq -r '.status // "unknown"' 2>/dev/null)
    echo "  Bot Status: $STATUS"
else
    echo "  FAIL: Root endpoint did not return expected response"
    echo "  Response: $ROOT_RESPONSE"
    FAILED=1
fi
echo ""

# Test 5: Messages Endpoint (expects 401 for unauthenticated)
echo "Test 5: Messages Endpoint Authentication"
HTTP_CODE=$(curl -s -k -o /dev/null -w "%{http_code}" -X POST "$BOT_URL/api/messages" \
  -H "Content-Type: application/json" \
  -d '{"type":"message","text":"test"}')

if [ "$HTTP_CODE" = "401" ]; then
    echo "  OK: Messages endpoint requires authentication (HTTP 401)"
    echo "  This is correct - Bot Framework validates bearer tokens"
elif [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "202" ]; then
    echo "  WARNING: Messages endpoint accepted unauthenticated request"
    echo "  This may indicate authentication is disabled"
else
    echo "  FAIL: Messages endpoint returned unexpected HTTP $HTTP_CODE"
    FAILED=1
fi
echo ""

# Test 6: Check VM is running (if we have Azure CLI access)
echo "Test 6: VM Status Check"
if command -v az &> /dev/null; then
    VM_STATE=$(az vm get-instance-view -g "$RG_NAME" -n "$VM_NAME" \
        --query "instanceView.statuses[1].displayStatus" -o tsv 2>/dev/null || echo "Unknown")
    if [ "$VM_STATE" = "VM running" ]; then
        echo "  OK: VM is running"
    elif [ "$VM_STATE" = "VM deallocated" ]; then
        echo "  FAIL: VM is deallocated (stopped)"
        echo "  To start: az vm start -g $RG_NAME -n $VM_NAME"
        FAILED=1
    else
        echo "  INFO: VM state is '$VM_STATE'"
    fi
else
    echo "  SKIP: Azure CLI not available"
fi
echo ""

# Summary
echo "=============================================="
if [ "$FAILED" -eq 0 ]; then
    echo "RESULT: All tests passed"
    exit 0
else
    echo "RESULT: Some tests failed"
    echo ""
    echo "Troubleshooting:"
    echo "  - Check VM logs: ./scripts/bot-logs.sh $ENV"
    echo "  - Restart service: ./scripts/bot-restart.sh $ENV"
    echo "  - For test env, start VM: az vm start -g TMinus15Agents-Test -n pennie-vm-test"
    exit 1
fi
