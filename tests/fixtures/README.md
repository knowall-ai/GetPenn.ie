# Test Fixtures

This directory contains test data for Pennie the Prepper automated tests.

## Structure

```
fixtures/
├── transcripts/          # Sample meeting transcripts
│   ├── happy_path/      # Clear requirements, single Epic
│   ├── multi_epic/      # Multiple Epics in one meeting
│   ├── ambiguous/       # Requirements with missing details
│   └── edge_cases/      # Overlapping speech, interruptions
├── audio/               # Sample audio files (WAV format)
├── expected_outputs/    # Expected work items JSON
└── README.md           # This file
```

## Transcript Format

Transcripts use this format:

```
Speaker: <Name> | Timestamp: <HH:MM:SS> | Text: <spoken text>
```

Example:
```
Speaker: Ben Weeks | Timestamp: 00:01:23 | Text: We need an epic for customer portal with SSO integration
```

## Categories

### Happy Path (`happy_path/`)

Clear, well-defined requirements with single Epic. Expected outcomes:
- 1 Epic created
- 2-4 Features under Epic
- User Stories with Given/When/Then acceptance criteria
- No ambiguity or questions

**Files**:
- `customer-portal-epic.txt` - Epic with SSO features
- Expected output: `../expected_outputs/customer-portal-epic.json`

### Multi-Epic (`multi_epic/`)

Discussion covering 2-3 different epics in same meeting. Expected outcomes:
- Multiple Epics created
- Features correctly grouped under appropriate Epics
- Parent-child relationships correct

**Files**: (To be added)

### Ambiguous (`ambiguous/`)

Requirements with missing details requiring clarification. Expected outcomes:
- Question work items created
- Ambiguity detection triggers
- Clarifying questions asked in chat

**Files**:
- `performance-requirement.txt` - Vague performance requirement ("fast")
- Expected: Question work item created

### Edge Cases (`edge_cases/`)

Challenging scenarios like overlapping speech, interruptions, long silences. Expected outcomes:
- Bot handles gracefully
- Transcription continues despite challenges
- Work items still created when possible

**Files**: (To be added)

## Audio Files (`audio/`)

Sample audio files for integration testing with Azure Speech Services.

**Format**: WAV, 16kHz, mono, 16-bit PCM

**Files**: (To be added - requires recording)

## Expected Outputs (`expected_outputs/`)

JSON files matching Azure DevOps work item structure.

**Format**:
```json
{
  "workItems": [
    {
      "type": "Epic",
      "title": "Work item title",
      "fields": {
        "ValueStatement": "...",
        "Speaker": "...",
        "Timestamp": "...",
        "MeetingID": "..."
      },
      "children": [...]
    }
  ]
}
```

## Usage in Tests

### Unit Tests

```python
def test_extract_epic_from_transcript():
    transcript = read_fixture("transcripts/happy_path/customer-portal-epic.txt")
    expected = read_fixture("expected_outputs/customer-portal-epic.json")

    result = agent.process_transcript(transcript)

    assert result.work_items[0].type == expected["workItems"][0]["type"]
    assert result.work_items[0].title == expected["workItems"][0]["title"]
```

### Integration Tests

```python
def test_end_to_end_transcript_processing():
    transcript = read_fixture("transcripts/happy_path/customer-portal-epic.txt")

    # Process line by line
    for line in transcript.split("\n"):
        result = parse_transcript_line(line)
        await agent.send_transcript(result)

    # Verify work items created in DevOps
    work_items = devops_client.get_work_items_by_meeting("test-meeting-001")
    assert len(work_items) == 5  # 1 Epic + 4 Features
```

## Contributing Test Fixtures

When adding new test fixtures:

1. **Clear naming**: Use descriptive names (e.g., `payment-processing-feature.txt`)
2. **Realistic content**: Base on actual requirements discussions
3. **Expected outputs**: Always provide corresponding expected output JSON
4. **Documentation**: Update this README with description

## Test Data Maintenance

- Review fixtures quarterly for relevance
- Add new scenarios as edge cases discovered
- Keep expected outputs in sync with agent behavior changes
