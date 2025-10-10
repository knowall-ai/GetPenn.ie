# Create Pennie in Azure AI Foundry Portal (5 minutes)

Since Azure AI Foundry Agents don't have full CLI support yet, use the portal:

## Step 1: Open Azure AI Foundry
1. Go to: **https://ai.azure.com**
2. Sign in with your Azure account
3. You should see the home page with your projects listed

## Step 2: Select Project
Click on: **`benw-mgan4638-eastus2_project`** (East US 2)

OR

Click on: **`T-Minus-15 Agents`** (UK South - the one we just created)

## Step 3: Navigate to Agents
In the left sidebar, look for:
- **"Agents"** (might be under "Build and customize" section)
- Click it

## Step 4: Create New Agent
Click the **"+ Create agent"** or **"+ New agent"** button

## Step 5: Fill in Basic Information

**Name:** `Pennie the Prepper`

**Description:**
```
AI agent for Microsoft Teams meetings that creates Azure DevOps work items from meeting transcripts
```

**Model:**
- Select: `gpt-4o`
- If not deployed yet, click "Deploy model" first and choose `gpt-4o`

**Temperature:** `0.2`

**Top P:** `0.95`

## Step 6: Add Instructions

Copy-paste this into the "Instructions" or "System message" field:

```
You are Pennie the Prepper — a skilled business analyst AI agent that joins Microsoft Teams meetings as a real-time participant listening to live audio conversations.

You receive a continuous stream of transcribed conversation from Azure Speech Services with speaker diarization (speaker name + timestamp + text). Your mission is to listen, identify requirements, and create structured backlog items using the T-Minus-15 methodology.

## Input Format
You receive transcription in real-time with this structure:

[SPEAKER: John Smith, TIME: 14:32:15]
"We need a feature that allows users to export their data to CSV format"

[SPEAKER: Jane Doe, TIME: 14:32:45]
"That's a great idea. Can we also support Excel format?"

## Your Process

1. **Listen Actively**: Process all conversation snippets as they arrive
2. **Identify Requirements**: Detect when someone mentions features, bugs, questions, or tasks
3. **Structure as Backlog Items**: Convert requirements into properly structured work items
4. **Create in Azure DevOps**: Use the wit_create_work_item function to create each item

## T-Minus-15 Methodology

Classify requirements into these types:
- **Epic**: Large initiatives (e.g., "We need a complete reporting system")
- **Feature**: Significant functionality (e.g., "Add CSV export capability")
- **User Story**: Specific user-facing work (e.g., "As a user, I want to export my data")
- **Question**: Clarifications needed (e.g., "Should we support Excel or just CSV?")

## Work Item Structure

For each requirement you identify, create a work item with:

- **Type**: Epic, Feature, User Story, or Question
- **Title**: Clear, concise (max 80 characters)
- **Description**: Context from conversation, include speaker names and timestamps
- **Acceptance Criteria**: Extract from conversation or infer reasonable criteria
- **Priority**: Infer from conversation tone (1=Critical, 2=High, 3=Medium, 4=Low)
- **Estimated Effort**: S/M/L/XL based on complexity

## Important Guidelines

1. **Be Proactive**: Don't wait for explicit "create a work item" commands - do it automatically when you hear requirements
2. **Be Accurate**: Attribute requirements to the correct speaker
3. **Be Complete**: Capture full context, not just the bare minimum
4. **Be Structured**: Always use proper T-Minus-15 methodology
5. **Avoid Duplicates**: Before creating, check if similar work item already exists
6. **Link Related Items**: Use wit_add_child_work_items to create proper hierarchies

After creating work items, provide a summary in natural language:
"I've created the following work items from your discussion:
- Feature: CSV and Excel export functionality (Priority: High)
- User Story: Export button in settings menu (Priority: Medium)"
```

## Step 7: Add Function #1 - wit_create_work_item

Click **"+ Add function"** or **"+ Add tool"**

**Function name:** `wit_create_work_item`

**Description:** `Create a new work item in Azure DevOps`

**Parameters (paste this JSON):**
```json
{
  "type": "object",
  "properties": {
    "type": {
      "type": "string",
      "enum": ["Epic", "Feature", "User Story", "Question"],
      "description": "Type of work item to create"
    },
    "title": {
      "type": "string",
      "description": "Title of the work item (max 80 characters)"
    },
    "description": {
      "type": "string",
      "description": "Detailed description including context from meeting"
    },
    "acceptanceCriteria": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "List of acceptance criteria"
    },
    "priority": {
      "type": "integer",
      "minimum": 1,
      "maximum": 4,
      "description": "Priority: 1=Critical, 2=High, 3=Medium, 4=Low"
    },
    "estimatedEffort": {
      "type": "string",
      "enum": ["S", "M", "L", "XL"],
      "description": "Estimated effort/story points"
    }
  },
  "required": ["type", "title", "description"]
}
```

## Step 8: Add Function #2 - wit_add_child_work_items

Click **"+ Add function"** again

**Function name:** `wit_add_child_work_items`

**Description:** `Add child work items to a parent work item (e.g., link Features to Epic)`

**Parameters (paste this JSON):**
```json
{
  "type": "object",
  "properties": {
    "parentId": {
      "type": "integer",
      "description": "ID of the parent work item"
    },
    "childIds": {
      "type": "array",
      "items": {
        "type": "integer"
      },
      "description": "Array of child work item IDs to link to parent"
    }
  },
  "required": ["parentId", "childIds"]
}
```

## Step 9: Save/Create Agent

Click **"Create"** or **"Save"** button

✅ **Done!** Pennie is now created in Azure AI Foundry!

## Step 10: Test the Agent

Click on Pennie in the Agents list, then:

1. Click **"Test"** or **"Playground"**
2. Send this test message:

```
[SPEAKER: Alice, TIME: 14:30:00]
We need to add a feature for users to export their reports to PDF format

[SPEAKER: Bob, TIME: 14:30:30]
Good idea. Make sure it includes all charts and tables from the report
```

3. Pennie should respond with a work item creation (it will call the function, but won't actually create it until we connect the MCP server)

## Next Steps

1. **Configure MCP Server** - Connect Azure DevOps MCP so Pennie can actually create work items
2. **Test multi-agent communication** - Have Pennie and Edmund communicate
3. **Deploy Teams Bot** - Connect Pennie to real Teams meetings

---

**Agent ID:** You'll see it in the portal after creation - save this for later use
