# GraphCallService Participant Notifications - Test Cases

## Issue
When processing Graph participant notifications, the code throws an `InvalidOperationException` because it expects a JSON Object but receives an Array.

## Test Cases

### Test Case 1: Participant Notification with Array resourceData
**Description**: Verify that participant update notifications (with array resourceData) are handled gracefully.

**Input Notification**:
```json
{
  "changeType": "updated",
  "resourceUrl": "/communications/calls/{callId}/participants",
  "resourceData": [
    {
      "@odata.type": "#microsoft.graph.participant",
      "info": {
        "identity": {
          "user": {
            "displayName": "John Doe",
            "id": "user-guid-123"
          }
        }
      },
      "mediaStreams": [
        {
          "sourceId": "1234",
          "mediaType": "audio"
        }
      ]
    }
  ]
}
```

**Expected Behavior**:
- No exception thrown
- Logs "Received participant notification (array) with 1 items for call {CallId}"
- Triggers background participant refresh via RefreshParticipantsAsync
- Transcription continues working normally

### Test Case 2: Call State Notification with Object resourceData
**Description**: Verify that call state change notifications (with object resourceData) continue to work as expected.

**Input Notification**:
```json
{
  "changeType": "updated",
  "resourceUrl": "/communications/calls/{callId}",
  "resourceData": {
    "state": "terminated",
    "resultInfo": {
      "code": 0,
      "subcode": 0,
      "message": "Normal call termination"
    }
  }
}
```

**Expected Behavior**:
- No exception thrown
- Logs "Call state changed to: terminated"
- Logs termination reason
- Cleans up call resources (removes from _activeCalls, _callIdToMeetingId, _audioCallbacks)

### Test Case 3: Unknown resourceData Type
**Description**: Verify that unexpected resourceData types are logged but don't crash the application.

**Input Notification**:
```json
{
  "changeType": "updated",
  "resourceUrl": "/communications/calls/{callId}/something",
  "resourceData": "unexpected-string-value"
}
```

**Expected Behavior**:
- No exception thrown
- Logs warning: "Unexpected resourceData format: String for changeType: updated, resource: /communications/calls/{callId}/something"
- Continues processing normally

## Steps to Reproduce Original Issue

1. Start a Teams meeting with Pennie bot
2. Have multiple participants join the meeting
3. Observe Graph API sending participant update notifications to the callback URL
4. The notification has `changeType: "updated"` and `resourceUrl: "/communications/calls/{callId}/participants"`
5. The `resourceData` field contains an array of participant objects
6. Original code at line 589 attempts `resourceData.TryGetProperty("state", ...)` assuming it's an object
7. System throws: `InvalidOperationException: The requested operation requires an element of type 'Object', but the target element has type 'Array'`

## Verification Steps After Fix

1. Deploy the fixed code
2. Start a Teams meeting with Pennie bot
3. Have participants join/leave the meeting
4. Check logs for:
   - "ResourceData type: Array for changeType: updated" (Debug level)
   - "Received participant notification (array) with X items for call {CallId}" (Info level)
   - "Participant item 0: Type=Object" (Debug level)
   - No InvalidOperationException errors
5. Verify transcription continues working without interruption
6. Verify speaker names are correctly identified (MSI-to-name mapping updated)

## Fix Summary

The fix adds type checking before accessing `resourceData` properties:

1. Check `resourceData.ValueKind` to determine if it's Object, Array, or other type
2. For Object: Handle call state changes (existing logic)
3. For Array: Handle participant updates (new logic)
   - Log the array length and first few items for debugging
   - Trigger background participant refresh
4. For other types: Log warning but don't crash

This ensures graceful handling of both notification types and maintains backward compatibility with existing call state change notifications.
