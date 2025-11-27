#!/bin/bash
# Test PennieBot via Direct Line API
# Usage: ./tests/bot-direct-line.sh [message] [wait_seconds]
#   message: Optional message to send (default: "What projects do we have in DevOps?")
#   wait_seconds: How long to wait for response (default: 15)

set -e

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
BOT_NAME="${BOT_NAME:-pennie-bot}"
MESSAGE="${1:-What projects do we have in DevOps?}"
WAIT_SECONDS="${2:-15}"

echo "Testing PennieBot via Direct Line API"
echo ""

# Get Direct Line secret
# Note: Azure only returns the key during CREATE, not SHOW (security feature)
# So we try CREATE first (it's idempotent) to get a fresh key
echo "Getting Direct Line secret..."
DIRECT_LINE_JSON=$(az bot directline create --resource-group "$RESOURCE_GROUP" --name "$BOT_NAME" --site-name default -o json 2>/dev/null)
DIRECT_LINE_SECRET=$(echo "$DIRECT_LINE_JSON" | jq -r '.properties.properties.sites[0].key // empty')

if [ -z "$DIRECT_LINE_SECRET" ]; then
    echo "FAIL: Could not get Direct Line secret"
    exit 1
fi

echo "OK: Direct Line secret obtained"

# Start a conversation
echo ""
echo "Starting conversation..."
CONV_RESPONSE=$(curl -s -X POST "https://directline.botframework.com/v3/directline/conversations" \
  -H "Authorization: Bearer $DIRECT_LINE_SECRET" \
  -H "Content-Type: application/json")

CONV_ID=$(echo "$CONV_RESPONSE" | jq -r '.conversationId')

if [ -z "$CONV_ID" ] || [ "$CONV_ID" = "null" ]; then
    echo "FAIL: Could not start conversation"
    echo "$CONV_RESPONSE" | jq .
    exit 1
fi

echo "OK: Conversation started: $CONV_ID"

# Send a message
echo ""
echo "Sending: \"$MESSAGE\""
# Use jq to properly escape the message and construct valid JSON
SEND_RESPONSE=$(jq -n \
  --arg msg "$MESSAGE" \
  '{type: "message", from: {id: "test-user", name: "Test User"}, text: $msg}' | \
  curl -s -X POST "https://directline.botframework.com/v3/directline/conversations/$CONV_ID/activities" \
    -H "Authorization: Bearer $DIRECT_LINE_SECRET" \
    -H "Content-Type: application/json" \
    -d @-)

ACTIVITY_ID=$(echo "$SEND_RESPONSE" | jq -r '.id')
ERROR_CODE=$(echo "$SEND_RESPONSE" | jq -r '.error.code // empty')

if [ -n "$ERROR_CODE" ]; then
    echo "FAIL: Bot returned error when receiving message"
    echo "Error: $(echo "$SEND_RESPONSE" | jq -r '.error.message')"
    echo ""
    echo "This typically means the bot has a configuration issue (e.g., invalid credentials)."
    echo "Check logs with: ./scripts/bot-logs.sh"
    exit 1
fi

if [ -z "$ACTIVITY_ID" ] || [ "$ACTIVITY_ID" = "null" ]; then
    echo "FAIL: Could not send message (unknown error)"
    echo "$SEND_RESPONSE" | jq .
    exit 1
fi

echo "OK: Message sent (activity: $ACTIVITY_ID)"

# Wait for response
echo ""
echo "Waiting ${WAIT_SECONDS}s for bot response..."
sleep "$WAIT_SECONDS"

# Get activities (bot's response)
echo ""
echo "Bot Response:"
echo "------------------------------------------------------------"
ACTIVITIES=$(curl -s "https://directline.botframework.com/v3/directline/conversations/$CONV_ID/activities" \
  -H "Authorization: Bearer $DIRECT_LINE_SECRET")

# Show bot responses (exclude user's message)
echo "$ACTIVITIES" | jq -r '.activities[] | select(.from.id != "test-user") | .text // "(no text - check attachments)"'

# Check if there was a response
BOT_MESSAGES=$(echo "$ACTIVITIES" | jq '[.activities[] | select(.from.id != "test-user")] | length')

echo "------------------------------------------------------------"
echo ""

if [ "$BOT_MESSAGES" -gt 0 ]; then
    echo "OK: Bot responded with $BOT_MESSAGES message(s)"
    exit 0
else
    echo "FAIL: No response from bot"
    echo ""
    echo "Check logs with: ./scripts/bot-logs.sh"
    exit 1
fi
