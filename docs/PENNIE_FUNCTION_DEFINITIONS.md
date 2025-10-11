# Pennie Function Definitions for Azure AI Foundry

This document contains the function definitions to add to Pennie in Azure AI Foundry Agents playground.

**Backend URL**: `https://pennie-backend-prod.azurewebsites.net`

**Total Functions**: 9

---

## How to Add Functions in AI Foundry

1. Go to your Pennie agent in AI Foundry
2. Click **"+ Add"** under Actions (4)
3. Choose **"Add action"** → **"Function"**
4. Copy-paste the function definition below
5. Update the endpoint URL if needed
6. Click **Save**

---

## Function 1: read_projects

**Name**: `read_projects`
**Description**: List all client projects in the Azure DevOps organization (26 KnowAll projects)
**Method**: GET
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_projects`

**Parameters**: None

**Response Schema**:
```json
{
  "success": true,
  "projects": [
    {
      "id": "string",
      "name": "string",
      "description": "string",
      "state": "string",
      "visibility": "string"
    }
  ]
}
```

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Projects
  version: 1.0.0
paths:
  /api/read_projects:
    get:
      operationId: read_projects
      summary: List all Azure DevOps projects
      description: Returns all 26 client projects in the KnowAll organization
      responses:
        '200':
          description: Successful response
          content:
            application/json:
              schema:
                type: object
                properties:
                  success:
                    type: boolean
                  projects:
                    type: array
                    items:
                      type: object
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 2: read_teams

**Name**: `read_teams`
**Description**: List all teams within a specific project
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_teams`

**Parameters**:
- `project` (string, required): Project name or ID

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Teams
  version: 1.0.0
paths:
  /api/read_teams:
    post:
      operationId: read_teams
      summary: List teams in a project
      description: Returns all teams within the specified Azure DevOps project
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
              properties:
                project:
                  type: string
                  description: Project name or ID
      responses:
        '200':
          description: Successful response
          content:
            application/json:
              schema:
                type: object
                properties:
                  success:
                    type: boolean
                  teams:
                    type: array
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 3: read_work_item

**Name**: `read_work_item`
**Description**: Get a single work item with full details by ID
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_work_item`

**Parameters**:
- `project` (string, required): Project name
- `workItemId` (integer, required): Work item ID number

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Work Item
  version: 1.0.0
paths:
  /api/read_work_item:
    post:
      operationId: read_work_item
      summary: Get single work item by ID
      description: Returns full details of a specific work item including fields, relations, comments
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
                - workItemId
              properties:
                project:
                  type: string
                workItemId:
                  type: integer
      responses:
        '200':
          description: Successful response
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 4: read_work_items

**Name**: `read_work_items`
**Description**: Get multiple work items with flexible filtering and recursive depth support
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_work_items`

**Parameters**:
- `project` (string, required): Project name
- `workItemIds` (array, optional): Specific work item IDs to retrieve
- `parentId` (integer, optional): Get children of this parent
- `depth` (integer, optional): Recursive depth (1-5, default 1)
- `workItemType` (string, optional): Filter by type (Epic, Feature, User Story, etc.)
- `state` (string, optional): Filter by state (New, Active, Resolved, Closed)
- `top` (integer, optional): Limit results (default 100, max 200)

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Work Items
  version: 1.0.0
paths:
  /api/read_work_items:
    post:
      operationId: read_work_items
      summary: Get multiple work items with flexible filtering
      description: |
        Retrieve work items with various filtering options:
        - Get specific IDs
        - Get children of parent (with recursive depth 1-5)
        - Filter by type and state

        Examples:
        - Get Epic with all Features and Stories: {"project": "ClientA", "parentId": 123, "depth": 3}
        - Get specific work items: {"project": "ClientA", "workItemIds": [100, 101, 102]}
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
              properties:
                project:
                  type: string
                workItemIds:
                  type: array
                  items:
                    type: integer
                parentId:
                  type: integer
                depth:
                  type: integer
                  minimum: 1
                  maximum: 5
                  default: 1
                workItemType:
                  type: string
                state:
                  type: string
                top:
                  type: integer
                  maximum: 200
      responses:
        '200':
          description: Successful response
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 5: read_work_item_types

**Name**: `read_work_item_types`
**Description**: Discover available work item types for a project
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_work_item_types`

**Parameters**:
- `project` (string, required): Project name

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Work Item Types
  version: 1.0.0
paths:
  /api/read_work_item_types:
    post:
      operationId: read_work_item_types
      summary: Discover available work item types
      description: Returns all work item types configured for the project (Epic, Feature, User Story, Bug, etc.)
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
              properties:
                project:
                  type: string
      responses:
        '200':
          description: Successful response
          content:
            application/json:
              schema:
                type: object
                properties:
                  workItemTypes:
                    type: array
                    items:
                      type: object
                      properties:
                        name:
                          type: string
                        description:
                          type: string
                        icon:
                          type: string
                        color:
                          type: string
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 6: read_link_types

**Name**: `read_link_types`
**Description**: Get available work item link types
**Method**: GET
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/read_link_types`

**Parameters**: None

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Read Link Types
  version: 1.0.0
paths:
  /api/read_link_types:
    get:
      operationId: read_link_types
      summary: Get available link types
      description: |
        Returns all link types Pennie can use to connect work items:
        - Hierarchy (Parent/Child)
        - Dependency (Predecessor/Successor)
        - Related
        - Duplicate
      responses:
        '200':
          description: Successful response
          content:
            application/json:
              schema:
                type: object
                properties:
                  linkTypes:
                    type: array
                    items:
                      type: object
                      properties:
                        name:
                          type: string
                        displayName:
                          type: string
                        description:
                          type: string
                        direction:
                          type: string
                        category:
                          type: string
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 7: search_work_items

**Name**: `search_work_items`
**Description**: WIQL-based advanced search with multiple filters
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/search_work_items`

**Parameters**:
- `project` (string, required): Project name
- `workItemType` (string, optional): Filter by type
- `state` (string, optional): Filter by state
- `assignedTo` (string, optional): Filter by assigned user
- `tags` (string, optional): Filter by tags
- `titleContains` (string, optional): Search in title
- `top` (integer, optional): Limit results (default 50)

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Search Work Items
  version: 1.0.0
paths:
  /api/search_work_items:
    post:
      operationId: search_work_items
      summary: WIQL-based search with advanced filtering
      description: |
        Search work items using multiple optional filters.
        All filters are combined with AND logic.

        Example: Find all Active Features assigned to Sarah
        {"project": "ClientA", "workItemType": "Feature", "state": "Active", "assignedTo": "sarah@example.com"}
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
              properties:
                project:
                  type: string
                workItemType:
                  type: string
                state:
                  type: string
                assignedTo:
                  type: string
                tags:
                  type: string
                titleContains:
                  type: string
                top:
                  type: integer
                  default: 50
      responses:
        '200':
          description: Successful response
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 8: create_work_item

**Name**: `create_work_item`
**Description**: Create new work items (Epic, Feature, User Story, Question)
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/create_work_item`

**Parameters**:
- `project` (string, required): Project name
- `workItemType` (string, required): Epic, Feature, User Story, or Question
- `title` (string, required): Work item title
- `description` (string, required): Detailed description
- `acceptanceCriteria` (array of strings, optional): Acceptance criteria
- `priority` (integer, optional): Priority level
- `estimatedEffort` (string, optional): Story points or hours

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Create Work Item
  version: 1.0.0
paths:
  /api/create_work_item:
    post:
      operationId: create_work_item
      summary: Create new work item
      description: Creates Epic, Feature, User Story, or Question in Azure DevOps
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
                - workItemType
                - title
                - description
              properties:
                project:
                  type: string
                workItemType:
                  type: string
                  enum: [Epic, Feature, "User Story", Question]
                title:
                  type: string
                description:
                  type: string
                acceptanceCriteria:
                  type: array
                  items:
                    type: string
                priority:
                  type: integer
                estimatedEffort:
                  type: string
      responses:
        '200':
          description: Work item created successfully
          content:
            application/json:
              schema:
                type: object
                properties:
                  success:
                    type: boolean
                  workItemId:
                    type: integer
                  workItemUrl:
                    type: string
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Function 9: link_work_items

**Name**: `link_work_items`
**Description**: Create flexible work item links with custom link types
**Method**: POST
**URL**: `https://pennie-backend-prod.azurewebsites.net/api/link_work_items`

**Parameters**:
- `project` (string, required): Project name
- `sourceId` (integer, required): Source work item ID
- `targetIds` (array of integers, required): Target work item IDs to link
- `linkType` (string, optional): Link type (default: System.LinkTypes.Hierarchy-Forward)
- `comment` (string, optional): Comment for the link

**Link Types Available**:
- `System.LinkTypes.Hierarchy-Forward` - Parent → Child (default)
- `System.LinkTypes.Hierarchy-Reverse` - Child → Parent
- `System.LinkTypes.Related` - Related items
- `System.LinkTypes.Dependency-Forward` - Predecessor → Successor
- `System.LinkTypes.Dependency-Reverse` - Successor → Predecessor
- `System.LinkTypes.Duplicate-Forward` - Mark as duplicate

**Backward Compatible**: Also accepts `parentId`/`childIds` for legacy support

**OpenAPI Spec**:
```yaml
openapi: 3.0.0
info:
  title: Link Work Items
  version: 1.0.0
paths:
  /api/link_work_items:
    post:
      operationId: link_work_items
      summary: Create flexible work item links
      description: |
        Link work items with various relationship types:
        - Hierarchy (Parent-Child for Epic → Feature → Story)
        - Dependencies (Task A must complete before Task B)
        - Related items (shared concerns)
        - Duplicates

        Backward compatible: Use parentId/childIds for simple parent-child links
      requestBody:
        required: true
        content:
          application/json:
            schema:
              type: object
              required:
                - project
                - sourceId
                - targetIds
              properties:
                project:
                  type: string
                sourceId:
                  type: integer
                targetIds:
                  type: array
                  items:
                    type: integer
                linkType:
                  type: string
                  default: "System.LinkTypes.Hierarchy-Forward"
                  enum:
                    - "System.LinkTypes.Hierarchy-Forward"
                    - "System.LinkTypes.Hierarchy-Reverse"
                    - "System.LinkTypes.Related"
                    - "System.LinkTypes.Dependency-Forward"
                    - "System.LinkTypes.Dependency-Reverse"
                    - "System.LinkTypes.Duplicate-Forward"
                comment:
                  type: string
                parentId:
                  type: integer
                  description: Legacy parameter (use sourceId instead)
                childIds:
                  type: array
                  items:
                    type: integer
                  description: Legacy parameter (use targetIds instead)
      responses:
        '200':
          description: Links created successfully
servers:
  - url: https://pennie-backend-prod.azurewebsites.net
```

---

## Quick Add Instructions

**For AI Foundry Portal:**

1. Click **"+ Add"** under Actions
2. Select **"Function"**
3. Paste the OpenAPI spec for each function above
4. Verify the server URL is correct
5. Save the function
6. Repeat for all 9 functions

**Authentication**: Functions use Azure Function-level authentication (already configured in backend)

---

## Testing in Playground

After adding functions, test with:

```
What DevOps projects do we have?
```

Pennie should call `read_projects()`, you click Submit, and she'll show all 26 KnowAll projects!

---

## Function Summary

| # | Function | Method | Purpose |
|---|----------|--------|---------|
| 1 | read_projects | GET | List all 26 projects |
| 2 | read_teams | POST | List teams in project |
| 3 | read_work_item | POST | Get single work item by ID |
| 4 | read_work_items | POST | Flexible filtering + recursive depth |
| 5 | read_work_item_types | POST | Discover available types |
| 6 | read_link_types | GET | Discover link types |
| 7 | search_work_items | POST | WIQL-based advanced search |
| 8 | create_work_item | POST | Create Epics/Features/Stories |
| 9 | link_work_items | POST | Flexible linking with types |

**Total**: 9 functions providing complete Azure DevOps integration

---

**Last Updated**: 2025-10-11
**Backend**: https://pennie-backend-prod.azurewebsites.net
**Status**: All functions deployed and tested ✅
