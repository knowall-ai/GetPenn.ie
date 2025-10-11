"""
Pennie the Prepper - Azure Function Backend
Handles function calls from Azure AI Foundry Agent and creates Azure DevOps work items
"""

import os
import json
import logging
import azure.functions as func
from typing import Dict, List, Any, Optional
import requests
from base64 import b64encode

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = func.FunctionApp()


class AzureDevOpsClient:
    """Client for Azure DevOps REST API with dynamic project support"""

    def __init__(self, organization: str, pat: str):
        self.organization = organization
        self.pat = pat
        self.api_version = "7.1"

        # Create authorization header
        auth_token = b64encode(f":{pat}".encode()).decode()
        self.headers = {
            "Authorization": f"Basic {auth_token}",
            "Content-Type": "application/json-patch+json",
            "Accept": "application/json"
        }

    def create_work_item(
        self,
        project: str,
        work_item_type: str,
        title: str,
        description: str,
        acceptance_criteria: Optional[List[str]] = None,
        priority: Optional[int] = None,
        estimated_effort: Optional[str] = None,
        tags: Optional[List[str]] = None,
        custom_fields: Optional[Dict[str, Any]] = None
    ) -> Dict[str, Any]:
        """
        Create a work item in Azure DevOps

        Args:
            project: Azure DevOps project name
            work_item_type: Epic, Feature, User Story, or Question
            title: Work item title
            description: Detailed description
            acceptance_criteria: List of acceptance criteria (for User Stories)
            priority: Priority level (1-4)
            estimated_effort: Effort estimate (e.g., "3", "5", "8")
            tags: List of tags
            custom_fields: Additional custom fields

        Returns:
            Created work item data
        """
        base_url = f"https://dev.azure.com/{self.organization}/{project}/_apis"
        url = f"{base_url}/wit/workitems/${work_item_type}?api-version={self.api_version}"

        # Build the JSON patch document
        operations = [
            {
                "op": "add",
                "path": "/fields/System.Title",
                "value": title
            },
            {
                "op": "add",
                "path": "/fields/System.Description",
                "value": description
            }
        ]

        # Add acceptance criteria for User Stories
        if acceptance_criteria and work_item_type == "User Story":
            criteria_html = "<div><strong>Acceptance Criteria:</strong><ul>"
            for criterion in acceptance_criteria:
                criteria_html += f"<li>{criterion}</li>"
            criteria_html += "</ul></div>"

            operations.append({
                "op": "add",
                "path": "/fields/Microsoft.VSTS.Common.AcceptanceCriteria",
                "value": criteria_html
            })

        # Add priority
        if priority:
            operations.append({
                "op": "add",
                "path": "/fields/Microsoft.VSTS.Common.Priority",
                "value": priority
            })

        # Add effort estimate
        if estimated_effort:
            operations.append({
                "op": "add",
                "path": "/fields/Microsoft.VSTS.Scheduling.Effort",
                "value": estimated_effort
            })

        # Add tags
        if tags:
            tags_str = "; ".join(tags)
            operations.append({
                "op": "add",
                "path": "/fields/System.Tags",
                "value": tags_str
            })

        # Add custom fields
        if custom_fields:
            for field_path, value in custom_fields.items():
                operations.append({
                    "op": "add",
                    "path": f"/fields/{field_path}",
                    "value": value
                })

        logger.info(f"Creating {work_item_type} in project '{project}': {title}")

        try:
            response = requests.post(url, headers=self.headers, json=operations)
            response.raise_for_status()
            work_item = response.json()

            logger.info(f"Created work item #{work_item['id']} in '{project}': {title}")
            return work_item

        except requests.exceptions.RequestException as e:
            logger.error(f"Failed to create work item in '{project}': {str(e)}")
            if hasattr(e, 'response') and e.response is not None:
                logger.error(f"Response: {e.response.text}")
            raise

    def add_child_work_items(self, project: str, parent_id: int, child_ids: List[int]) -> Dict[str, Any]:
        """
        Link child work items to a parent work item

        Args:
            project: Azure DevOps project name
            parent_id: Parent work item ID
            child_ids: List of child work item IDs

        Returns:
            Result of linking operation
        """
        base_url = f"https://dev.azure.com/{self.organization}/{project}/_apis"
        results = []

        for child_id in child_ids:
            url = f"{base_url}/wit/workitems/{parent_id}?api-version={self.api_version}"

            operations = [{
                "op": "add",
                "path": "/relations/-",
                "value": {
                    "rel": "System.LinkTypes.Hierarchy-Forward",
                    "url": f"https://dev.azure.com/{self.organization}/{project}/_apis/wit/workItems/{child_id}",
                    "attributes": {
                        "comment": "Linked by Pennie the Prepper"
                    }
                }
            }]

            try:
                response = requests.patch(url, headers=self.headers, json=operations)
                response.raise_for_status()

                logger.info(f"Linked child #{child_id} to parent #{parent_id} in '{project}'")
                results.append({
                    "parent_id": parent_id,
                    "child_id": child_id,
                    "success": True
                })

            except requests.exceptions.RequestException as e:
                logger.error(f"Failed to link #{child_id} to #{parent_id} in '{project}': {str(e)}")
                results.append({
                    "parent_id": parent_id,
                    "child_id": child_id,
                    "success": False,
                    "error": str(e)
                })

        return {"results": results}


# Initialize Azure DevOps client
def get_devops_client() -> AzureDevOpsClient:
    """Get configured Azure DevOps client"""
    org = os.getenv("AZURE_DEVOPS_ORG")
    pat = os.getenv("AZURE_DEVOPS_PAT")

    if not all([org, pat]):
        raise ValueError("Missing Azure DevOps configuration. Set AZURE_DEVOPS_ORG and AZURE_DEVOPS_PAT")

    return AzureDevOpsClient(org, pat)


@app.route(route="create_work_item", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
def create_work_item(req: func.HttpRequest) -> func.HttpResponse:
    """
    Azure Function endpoint to create work items
    Handles function calls from Pennie the Prepper agent
    """
    logger.info("Received create_work_item request")

    try:
        # Parse request body
        req_body = req.get_json()
        logger.info(f"Request body: {json.dumps(req_body, indent=2)}")

        # Extract parameters
        project = req_body.get("project")
        work_item_type = req_body.get("type")
        title = req_body.get("title")
        description = req_body.get("description")
        acceptance_criteria = req_body.get("acceptanceCriteria")
        priority = req_body.get("priority")
        estimated_effort = req_body.get("estimatedEffort")

        # Validate required fields
        if not all([project, work_item_type, title, description]):
            return func.HttpResponse(
                json.dumps({"error": "Missing required fields: project, type, title, description"}),
                status_code=400,
                mimetype="application/json"
            )

        # Extract metadata from description if present (speaker, timestamp, meeting ID)
        tags = []
        custom_fields = {}

        # Get DevOps client and create work item
        client = get_devops_client()
        work_item = client.create_work_item(
            project=project,
            work_item_type=work_item_type,
            title=title,
            description=description,
            acceptance_criteria=acceptance_criteria,
            priority=priority,
            estimated_effort=estimated_effort,
            tags=tags,
            custom_fields=custom_fields
        )

        # Return success response
        return func.HttpResponse(
            json.dumps({
                "success": True,
                "work_item_id": work_item["id"],
                "work_item_type": work_item["fields"]["System.WorkItemType"],
                "title": work_item["fields"]["System.Title"],
                "url": work_item["_links"]["html"]["href"],
                "project": project
            }),
            status_code=200,
            mimetype="application/json"
        )

    except ValueError as e:
        logger.error(f"Validation error: {str(e)}")
        return func.HttpResponse(
            json.dumps({"error": str(e)}),
            status_code=400,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error creating work item: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="link_work_items", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
def link_work_items(req: func.HttpRequest) -> func.HttpResponse:
    """
    Azure Function endpoint to link child work items to a parent
    Handles function calls from Pennie the Prepper agent
    """
    logger.info("Received link_work_items request")

    try:
        # Parse request body
        req_body = req.get_json()
        logger.info(f"Request body: {json.dumps(req_body, indent=2)}")

        # Extract parameters
        project = req_body.get("project")
        parent_id = req_body.get("parentId")
        child_ids = req_body.get("childIds")

        # Validate required fields
        if not project or not parent_id or not child_ids:
            return func.HttpResponse(
                json.dumps({"error": "Missing required fields: project, parentId, childIds"}),
                status_code=400,
                mimetype="application/json"
            )

        # Get DevOps client and link work items
        client = get_devops_client()
        result = client.add_child_work_items(project, parent_id, child_ids)

        # Return success response
        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "parent_id": parent_id,
                "linked_children": [r for r in result["results"] if r["success"]],
                "failed_links": [r for r in result["results"] if not r["success"]]
            }),
            status_code=200,
            mimetype="application/json"
        )

    except ValueError as e:
        logger.error(f"Validation error: {str(e)}")
        return func.HttpResponse(
            json.dumps({"error": str(e)}),
            status_code=400,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error linking work items: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="read_projects", methods=["GET"], auth_level=func.AuthLevel.FUNCTION)
def read_projects(req: func.HttpRequest) -> func.HttpResponse:
    """
    Read all Azure DevOps projects
    Helps Pennie discover available client projects
    """
    logger.info("Received read_projects request")

    try:
        client = get_devops_client()
        url = f"https://dev.azure.com/{client.organization}/_apis/projects?api-version={client.api_version}"

        response = requests.get(url, headers=client.headers)
        response.raise_for_status()
        projects_data = response.json()

        projects = [
            {
                "name": proj["name"],
                "id": proj["id"],
                "description": proj.get("description", ""),
                "visibility": proj.get("visibility", "private")
            }
            for proj in projects_data.get("value", [])
        ]

        logger.info(f"Retrieved {len(projects)} projects")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "count": len(projects),
                "projects": projects
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error listing projects: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="read_teams", methods=["POST"], auth_level=func.AuthLevel.FUNCTION)
def read_teams(req: func.HttpRequest) -> func.HttpResponse:
    """
    Read all teams in a specific Azure DevOps project
    Helps Pennie understand team structure
    """
    logger.info("Received read_teams request")

    try:
        req_body = req.get_json()
        project = req_body.get("project")

        if not project:
            return func.HttpResponse(
                json.dumps({"error": "Missing required field: project"}),
                status_code=400,
                mimetype="application/json"
            )

        client = get_devops_client()
        url = f"https://dev.azure.com/{client.organization}/_apis/projects/{project}/teams?api-version={client.api_version}"

        response = requests.get(url, headers=client.headers)
        response.raise_for_status()
        teams_data = response.json()

        teams = [
            {
                "name": team["name"],
                "id": team["id"],
                "description": team.get("description", "")
            }
            for team in teams_data.get("value", [])
        ]

        logger.info(f"Retrieved {len(teams)} teams from project '{project}'")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "count": len(teams),
                "teams": teams
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error listing teams: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="health", methods=["GET"], auth_level=func.AuthLevel.ANONYMOUS)
def health_check(req: func.HttpRequest) -> func.HttpResponse:
    """Health check endpoint"""
    return func.HttpResponse(
        json.dumps({"status": "healthy", "service": "Pennie Backend"}),
        status_code=200,
        mimetype="application/json"
    )
