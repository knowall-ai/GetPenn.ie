# Preppie Backend - Azure Functions Service

This directory contains the Azure Functions backend service that handles function calls from Preppie the Prepper AI agent and creates Azure DevOps work items.

## Architecture

```
Preppie (Azure AI Foundry Agent)
    ↓ Function Call
Azure Functions Backend (this service)
    ↓ REST API
Azure DevOps REST API
    ↓ Work Item Created
Azure DevOps Project
```

## Functions

### 1. `wit_create_work_item`
Creates a work item in Azure DevOps.

**Endpoint**: `POST /api/wit_create_work_item`

**Request Body**:
```json
{
  "type": "Epic|Feature|User Story|Question",
  "title": "Work item title",
  "description": "Detailed description",
  "acceptanceCriteria": ["Criterion 1", "Criterion 2"],  // Optional
  "priority": 1,  // Optional: 1-4
  "estimatedEffort": "5"  // Optional
}
```

**Response**:
```json
{
  "success": true,
  "work_item_id": 123,
  "work_item_type": "User Story",
  "title": "Work item title",
  "url": "https://dev.azure.com/org/project/_workitems/edit/123"
}
```

### 2. `wit_add_child_work_items`
Links child work items to a parent work item.

**Endpoint**: `POST /api/wit_add_child_work_items`

**Request Body**:
```json
{
  "parentId": 100,
  "childIds": [101, 102, 103]
}
```

**Response**:
```json
{
  "success": true,
  "parent_id": 100,
  "linked_children": [
    {"parent_id": 100, "child_id": 101, "success": true},
    {"parent_id": 100, "child_id": 102, "success": true}
  ],
  "failed_links": []
}
```

### 3. `health`
Health check endpoint.

**Endpoint**: `GET /api/health`

**Response**:
```json
{
  "status": "healthy",
  "service": "Preppie Backend"
}
```

## Deployment

### Prerequisites
- Azure CLI installed and configured
- Azure DevOps organization and project
- Azure DevOps Personal Access Token (PAT) with Work Items (Read, Write) permissions

### Environment Variables
Set these in your `.env` file:
```bash
AZURE_DEVOPS_ORG=your-org-name
AZURE_DEVOPS_PROJECT=your-project-name
AZURE_DEVOPS_PAT=your-personal-access-token
AZURE_RESOURCE_GROUP=TMinus15Agents
AZURE_LOCATION=uksouth
```

### Deploy
```bash
# Run the deployment script
./scripts/deploy-backend.sh
```

The script will:
1. Deploy Azure Function infrastructure (Storage Account, App Service Plan, Function App)
2. Deploy the function code
3. Configure environment variables
4. Retrieve function keys
5. Update your `.env` file with endpoints and keys

## Local Testing

### Prerequisites
- Python 3.11+
- Azure Functions Core Tools

### Setup
```bash
# Install dependencies
pip install -r requirements.txt

# Create local.settings.json
cat > local.settings.json << EOF
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "python",
    "AZURE_DEVOPS_ORG": "your-org",
    "AZURE_DEVOPS_PROJECT": "your-project",
    "AZURE_DEVOPS_PAT": "your-pat",
    "LOG_LEVEL": "INFO"
  }
}
EOF

# Start the function locally
func start
```

### Test with curl
```bash
# Test health check
curl http://localhost:7071/api/health

# Test creating a work item
curl -X POST http://localhost:7071/api/wit_create_work_item \
  -H "Content-Type: application/json" \
  -d '{
    "type": "User Story",
    "title": "Test Work Item",
    "description": "This is a test",
    "acceptanceCriteria": ["Given a user", "When they test", "Then it works"]
  }'

# Test linking work items (replace IDs with real ones)
curl -X POST http://localhost:7071/api/wit_add_child_work_items \
  -H "Content-Type: application/json" \
  -d '{
    "parentId": 123,
    "childIds": [124, 125]
  }'
```

## Authentication

The Azure Function uses **Function Key authentication**. After deployment, the function key is automatically retrieved and stored in your `.env` file.

To call the endpoints, include the function key in the header:
```bash
curl -H "x-functions-key: YOUR_FUNCTION_KEY" \
  https://your-function-app.azurewebsites.net/api/wit_create_work_item
```

## Monitoring

The function logs are sent to Application Insights. View logs in:
- Azure Portal → Function App → Monitor → Logs
- Application Insights → Transaction Search
- Azure CLI: `az functionapp logs tail --name pennie-backend-prod --resource-group TMinus15Agents`

## Azure DevOps API Reference

This service uses the Azure DevOps REST API v7.1:
- [Work Items API](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items)
- [Work Item Relations](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update)

## Troubleshooting

### Error: "Missing Azure DevOps configuration"
Ensure `AZURE_DEVOPS_ORG`, `AZURE_DEVOPS_PROJECT`, and `AZURE_DEVOPS_PAT` are set in environment variables.

### Error: "Unauthorized" from Azure DevOps
Check that your PAT has the correct permissions (Work Items: Read, Write).

### Function not responding
Wait 2-3 minutes after deployment for cold start. Check function logs for errors.

## Development

### Adding a New Function
1. Add a new function in `function_app.py`
2. Use the `@app.route()` decorator
3. Update this README with the new endpoint
4. Deploy using `./scripts/deploy-backend.sh`

### Code Structure
- `function_app.py` - Main Azure Functions application
- `AzureDevOpsClient` - Client for Azure DevOps REST API
- Error handling and logging throughout

## License
MIT
