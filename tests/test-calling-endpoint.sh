#!/bin/bash
# Test the /api/calling endpoint on the production bot
# This endpoint handles Graph Communications SDK notifications for meeting audio capture

set -e

BOT_URL="${BOT_URL:-https://pennie-prod-mmdxqm3w7kjwm.uksouth.cloudapp.azure.com}"

echo "Testing Bot Calling Endpoint"
echo ""
echo "Endpoint: $BOT_URL/api/calling"
echo ""

# Test 1: GET /api/calling (health check)
echo "Test 1: Health check (GET /api/calling)"
HTTP_CODE=$(curl -s -o /tmp/calling-response.json -w "%{http_code}" "$BOT_URL/api/calling")

if [ "$HTTP_CODE" = "200" ]; then
    echo "OK: Health check returned 200"
    echo "Response: $(cat /tmp/calling-response.json)"
else
    echo "FAIL: Health check returned $HTTP_CODE (expected 200)"
    cat /tmp/calling-response.json 2>/dev/null
    exit 1
fi

echo ""

# Test 2: POST /api/calling with empty notification
echo "Test 2: POST notification (empty body)"
HTTP_CODE=$(curl -s -o /tmp/calling-post-response.json -w "%{http_code}" \
    -X POST "$BOT_URL/api/calling" \
    -H "Content-Type: application/json" \
    -d '{}')

# Accept 200 (success) or 500 (expected when parsing empty notification)
if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "500" ]; then
    echo "OK: POST returned $HTTP_CODE (endpoint is reachable)"
else
    echo "FAIL: POST returned $HTTP_CODE (unexpected)"
    cat /tmp/calling-post-response.json 2>/dev/null
    exit 1
fi

echo ""

# Test 3: POST /api/calling/media (media notifications endpoint)
echo "Test 3: POST media notification"
HTTP_CODE=$(curl -s -o /tmp/calling-media-response.json -w "%{http_code}" \
    -X POST "$BOT_URL/api/calling/media" \
    -H "Content-Type: application/json" \
    -d '{}')

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "500" ]; then
    echo "OK: Media POST returned $HTTP_CODE (endpoint is reachable)"
else
    echo "FAIL: Media POST returned $HTTP_CODE (unexpected)"
    cat /tmp/calling-media-response.json 2>/dev/null
    exit 1
fi

echo ""
echo "------------------------------------------------------------"
echo "All calling endpoint tests passed!"
echo ""
echo "Note: Full audio functionality requires:"
echo "  - Graph Communications SDK (Windows only)"
echo "  - Azure AD permissions (Calls.AccessMedia.All, Calls.JoinGroupCall.All)"
echo "  - Bot to be invited to a Teams meeting"
