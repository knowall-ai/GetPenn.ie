# Pennie Backend - Session Summary
**Date**: October 11, 2025
**Session**: Multi-client Azure DevOps Integration

## ✅ Major Accomplishments

### 1. Complete Azure Functions Backend (6 Functions)
Created standardized CRUD-named functions for Azure DevOps integration:

| Function | Endpoint | Purpose |
|----------|----------|---------|
| `read_projects` | GET `/api/read_projects` | List all Azure DevOps projects (26 clients) |
| `read_teams` | POST `/api/read_teams` | List teams in a specific project |
| `read_work_item` | POST `/api/read_work_item` | Get single work item with full details |
| `read_child_work_items` | POST `/api/read_child_work_items` | Get child items (Features/Stories) |
| `create_work_item` | POST `/api/create_work_item` | Create work items (Epic/Feature/Story/Question) |
| `link_work_items` | POST `/api/link_work_items` | Link parent-child relationships |

**Location**: [src/function_app.py](src/function_app.py)

### 2. Dynamic Multi-Client Support
- **26 Client Projects** supported in KnowAll organization
- Dynamic `project` parameter in all functions
- Pennie can discover projects via `read_projects()` at meeting start
- Client projects include: HSE, Flogas, Cairn Homes, HRB, Plutus, Internal, and 20 others

### 3. Pennie Agent Configuration Updated
- **Instructions**: Tells Pennie to call `read_projects()` at meeting start
- **4 Function Definitions**: read_projects, read_teams, create_work_item, link_work_items
- **Client Project Logic**: Identifies which client from Teams channel, meeting context, or asks user
- **Agent ID**: `asst_QP4Q94razJnAaC16jjiuDfih`
- **Model**: gpt-4o (2024-08-06)
- **Region**: East US 2

### 4. Knowledge Sources Added
Created [knowledge-sources.json](knowledge-sources.json) with 4 sources:
- T-Minus-15 Methodology Repository (GitHub)
- Azure AI Foundry Documentation (Web crawler)
- DevOps Best Practices (Curated content)
- Azure DevOps REST API Reference (API documentation)

### 5. Infrastructure Deployed
- **Function App**: `pennie-backend-prod`
- **URL**: https://pennie-backend-prod.azurewebsites.net
- **Storage Account**: `penniebemmdxqm3w7kjwm`
- **Region**: UK South
- **Resource Group**: TMinus15Agents

### 6. Standardized Naming Conventions
Refactored all function names to follow REST/CRUD standards:
- **read_** for GET operations (read_projects, read_teams, read_work_item, read_child_work_items)
- **create_** for POST operations (create_work_item)
- **link_** for relationship operations (link_work_items)

### 7. Configuration Files
- `.env` - Environment variables with Azure DevOps PAT
- `.env.example` - Template for new developers
- `requirements.txt` - Python dependencies
- `host.json` - Azure Functions configuration
- `.funcignore` - Deployment exclusions
- `knowledge-sources.json` - Pennie's knowledge sources

## 📂 Files Created/Modified

### New Files:
```
src/
├── function_app.py (467 lines)
├── README.md
└── [modular refactoring pending]

infra/
└── deploy-function-app.bicep

scripts/
└── deploy-backend.sh

Root:
├── requirements.txt
├── host.json
├── .funcignore
├── .env.example
└── knowledge-sources.json
```

### Modified Files:
- `.env` - Added Azure DevOps org and PAT
- `DEPLOYMENT_STATUS.md` - Updated with backend details

## 🔄 Pending Tasks

### High Priority:
1. **Modular Refactoring** - Break [src/function_app.py](src/function_app.py) into separate files:
   ```
   src/
   ├── function_app.py (main entry)
   ├── shared/
   │   ├── devops_client.py
   │   └── utils.py
   └── functions/
       ├── read_projects.py
       ├── read_teams.py
       ├── read_work_item.py
       ├── read_child_work_items.py
       ├── create_work_item.py
       ├── link_work_items.py
       └── health.py
   ```

2. **Logic App Creation** - Create Power Automate/Logic App for Epic documentation:
   - Triggered manually by user
   - Calls our Azure Functions (read_work_item, read_child_work_items)
   - Generates Word/Excel documents
   - Calculates effort estimates
   - Sends Teams notifications

3. **Testing** - Test all 6 endpoints with real KnowAll DevOps data

4. **Function App Cold Start** - Investigate why function host not starting properly

### Medium Priority:
5. **Update Pennie with 2 new functions** - Add read_work_item and read_child_work_items to Pennie's configuration
6. **Knowledge Sources Integration** - Implement vector store/file search in Azure AI Foundry
7. **Commit & Push** - Push all changes to GitHub (currently uncommitted)

### Low Priority:
8. **Documentation** - Create API documentation for all 6 functions
9. **Error Handling** - Add more robust error handling and retry logic
10. **Monitoring** - Set up Application Insights dashboards

## 🔑 Key Technical Decisions

### 1. OAuth Scope Discovery
- **Wrong**: `https://cognitiveservices.azure.com`
- **Correct**: `https://ai.azure.com/.default`
- This was critical for Azure AI Foundry Agents API

### 2. Model Compatibility
- gpt-5-chat (2025-08-07) - ❌ Not compatible with Agents
- gpt-4o (2024-08-06) - ✅ Compatible

### 3. Dynamic vs. Static Projects
- Chose dynamic `project` parameter over hardcoded project
- Allows Pennie to work with all 26 clients
- Pennie discovers projects at runtime via `read_projects()`

### 4. Standard CRUD Naming
- Follows REST conventions
- read_ / create_ / update_ / delete_ / link_
- Makes API intuitive and consistent

## 🐛 Known Issues

1. **Function Host Not Starting** - Cold start issue, needs investigation
2. **Background Processes** - Multiple old deployments still running (should be cleaned up)
3. **Logic App Not Created** - Planned but not yet implemented
4. **No Automated Tests** - No unit or integration tests yet

## 🔐 Security

- Azure DevOps PAT stored securely in `.env` (not committed)
- `.env.example` has placeholder values
- Function App uses Function Key authentication
- OAuth tokens have proper scopes

## 📊 Statistics

- **Lines of Code**: ~600 lines in function_app.py
- **Functions**: 6 Azure Functions + 1 health check
- **Client Projects**: 26 supported
- **Deployment Time**: ~3 minutes for infrastructure
- **Model**: gpt-4o with 128k context window

## 🎯 Next Session Goals

1. ✅ Modular refactoring of [src/function_app.py](src/function_app.py)
2. ✅ Create Logic App for Epic documentation workflow
3. ✅ Test all endpoints with real data
4. ✅ Fix function host cold start issue
5. ✅ Commit and push all changes

## 📝 Notes

- All function endpoints require authentication (Function Key or Azure AD)
- Pennie agent ID: `asst_QP4Q94razJnAaC16jjiuDfih`
- Function App URL: https://pennie-backend-prod.azurewebsites.net
- Azure DevOps Org: KnowAll
- Resource Group: TMinus15Agents

---

**Session Duration**: ~3 hours
**Deployment Status**: ✅ Backend deployed, ⏳ Logic App pending
**Git Status**: Uncommitted changes (waiting for modular refactoring)
