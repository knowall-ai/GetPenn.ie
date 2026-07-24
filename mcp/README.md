# Azure DevOps MCP Server Configuration

This directory contains the configuration for Microsoft's official [Azure DevOps MCP Server](https://github.com/microsoft/azure-devops-mcp), which enables Preppie to interact with Azure DevOps work items through the Model Context Protocol.

## Overview

The Azure DevOps MCP Server runs co-located on the Windows Server VM alongside the Teams Media Bot. It provides a standardized interface for AI agents to create, read, update, and link work items in Azure DevOps.

## Installation

The MCP server is installed globally on the Windows VM during deployment:

```powershell
# Install Node.js 20+ (prerequisite)
choco install nodejs-lts

# Install Azure DevOps MCP Server
npm install -g @azure-devops/mcp
```

## Configuration

The MCP server is configured via [`mcp-config.json`](mcp-config.json):

```json
{
  "mcpServers": {
    "azure-devops": {
      "command": "npx",
      "args": ["-y", "@azure-devops/mcp", "${AZURE_DEVOPS_ORG}"],
      "domains": ["work-items"],
      "env": {
        "AZURE_DEVOPS_ORG": "${AZURE_DEVOPS_ORG}",
        "AZURE_DEVOPS_PROJECT": "${AZURE_DEVOPS_PROJECT}"
      }
    }
  }
}
```

## Environment Variables

Set these in the Windows VM environment or via `.env` file:

| Variable | Description | Example |
|----------|-------------|---------|
| `AZURE_DEVOPS_ORG` | Azure DevOps organization name | `knowallai` |
| `AZURE_DEVOPS_PROJECT` | Azure DevOps project name | `Preppie` |

## Authentication

The MCP server authenticates using Microsoft account login. During first run:

1. MCP server prompts for authentication
2. Opens browser for Microsoft account sign-in
3. Grants access to Azure DevOps organization
4. Credentials cached locally for subsequent runs

## Available Work Item Tools

The MCP server exposes these tools to Azure AI Foundry Agent:

### Create & Update
- `wit_create_work_item` - Create new work items (Epic, Feature, User Story, Question)
- `wit_update_work_item` - Update existing work item fields
- `wit_add_child_work_items` - Create parent-child relationships

### Read & Query
- `wit_get_work_item` - Retrieve single work item by ID
- `wit_get_work_items_batch_by_ids` - Retrieve multiple work items
- `wit_my_work_items` - List current user's assigned work items
- `wit_list_backlog_work_items` - Retrieve backlog items for project/team

### Comments & Links
- `wit_add_work_item_comment` - Add comments to work items
- `wit_list_work_item_comments` - Retrieve comments for work item
- `wit_link_work_item_to_pull_request` - Link work items to PRs

## Domain-Based Loading

The configuration loads only the `work-items` domain to optimize performance:

```json
"domains": ["work-items"]
```

Available domains:
- `work-items` - Work item operations (used by Preppie)
- `repositories` - Git repository operations
- `pipelines` - CI/CD pipeline operations
- `wiki` - Wiki page operations
- `test-plans` - Test management

## Testing

Test the MCP server connection:

```powershell
# Verify installation
npx @azure-devops/mcp --version

# Test connection to Azure DevOps
npx @azure-devops/mcp ${AZURE_DEVOPS_ORG} --test
```

## Integration with Azure AI Foundry

The MCP server is configured in Preppie's `agent-config.json`:

```json
{
  "tools": [
    {
      "type": "mcp_server",
      "server_id": "azure-devops",
      "server_url": "stdio",
      "domains": ["work-items"]
    }
  ]
}
```

Azure AI Foundry Agent automatically discovers and uses the available tools through the MCP protocol.

## Troubleshooting

See [docs/TROUBLESHOOTING.adoc](../docs/TROUBLESHOOTING.adoc) for comprehensive troubleshooting guide.

**Quick fixes**:

```powershell
# Clear cached credentials and re-authenticate
Remove-Item -Path "$env:USERPROFILE\.azure-devops-mcp" -Recurse -Force
npx @azure-devops/mcp ${AZURE_DEVOPS_ORG}

# Test connection
npx @azure-devops/mcp ${AZURE_DEVOPS_ORG} --test

# Update to latest version
npm update -g @azure-devops/mcp
```

## Resources

- [Azure DevOps MCP Server GitHub](https://github.com/microsoft/azure-devops-mcp)
- [Model Context Protocol Specification](https://modelcontextprotocol.io/)
- [Azure AI Foundry MCP Integration](https://learn.microsoft.com/en-us/azure/ai-foundry/agents/how-to/tools/model-context-protocol)
- [Azure DevOps REST API Documentation](https://learn.microsoft.com/en-us/rest/api/azure/devops/)

## License

The Azure DevOps MCP Server is licensed under MIT by Microsoft Corporation.
