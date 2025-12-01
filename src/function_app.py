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

    def link_work_items(
        self,
        project: str,
        source_id: int,
        target_ids: List[int],
        link_type: str = "System.LinkTypes.Hierarchy-Forward",
        comment: Optional[str] = None
    ) -> Dict[str, Any]:
        """
        Link work items with flexible link types

        Args:
            project: Azure DevOps project name
            source_id: Source work item ID
            target_ids: List of target work item IDs to link
            link_type: Link type (default: Hierarchy-Forward for parent-child)
                Common types:
                - System.LinkTypes.Hierarchy-Forward (Parent → Child)
                - System.LinkTypes.Hierarchy-Reverse (Child → Parent)
                - System.LinkTypes.Related (Related to)
                - System.LinkTypes.Dependency-Forward (Predecessor)
                - System.LinkTypes.Dependency-Reverse (Successor)
            comment: Optional comment for the link

        Returns:
            Result of linking operation
        """
        base_url = f"https://dev.azure.com/{self.organization}/{project}/_apis"
        results = []

        # Default comment based on link type
        if comment is None:
            comment = f"Linked by Pennie the Prepper ({link_type})"

        for target_id in target_ids:
            url = f"{base_url}/wit/workitems/{source_id}?api-version={self.api_version}"

            operations = [{
                "op": "add",
                "path": "/relations/-",
                "value": {
                    "rel": link_type,
                    "url": f"https://dev.azure.com/{self.organization}/{project}/_apis/wit/workItems/{target_id}",
                    "attributes": {
                        "comment": comment
                    }
                }
            }]

            try:
                response = requests.patch(url, headers=self.headers, json=operations)
                response.raise_for_status()

                logger.info(f"Linked work item #{target_id} to #{source_id} in '{project}' with link type '{link_type}'")
                results.append({
                    "source_id": source_id,
                    "target_id": target_id,
                    "link_type": link_type,
                    "success": True
                })

            except requests.exceptions.RequestException as e:
                logger.error(f"Failed to link #{target_id} to #{source_id} in '{project}': {str(e)}")
                results.append({
                    "source_id": source_id,
                    "target_id": target_id,
                    "link_type": link_type,
                    "success": False,
                    "error": str(e)
                })

        return {"results": results}

    # Keep backward compatibility
    def add_child_work_items(self, project: str, parent_id: int, child_ids: List[int]) -> Dict[str, Any]:
        """
        Legacy method - wraps link_work_items for backward compatibility
        Links child work items to a parent (Hierarchy-Forward)
        """
        return self.link_work_items(
            project=project,
            source_id=parent_id,
            target_ids=child_ids,
            link_type="System.LinkTypes.Hierarchy-Forward",
            comment="Linked by Pennie the Prepper (parent-child)"
        )


# Initialize Azure DevOps client
def get_devops_client() -> AzureDevOpsClient:
    """Get configured Azure DevOps client"""
    org = os.getenv("AZURE_DEVOPS_ORG")
    pat = os.getenv("AZURE_DEVOPS_PAT")

    if not all([org, pat]):
        raise ValueError("Missing Azure DevOps configuration. Set AZURE_DEVOPS_ORG and AZURE_DEVOPS_PAT")

    return AzureDevOpsClient(org, pat)


@app.route(route="create_work_item", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
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


@app.route(route="link_work_items", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
def link_work_items_endpoint(req: func.HttpRequest) -> func.HttpResponse:
    """
    Azure Function endpoint to link work items with flexible link types
    Handles function calls from Pennie the Prepper agent

    Parameters:
    - project (required): Project name
    - sourceId (required): Source work item ID
    - targetIds (required): Array of target work item IDs
    - linkType (optional): Link type (default: System.LinkTypes.Hierarchy-Forward)
      Common types:
      - System.LinkTypes.Hierarchy-Forward (Parent → Child)
      - System.LinkTypes.Hierarchy-Reverse (Child → Parent)
      - System.LinkTypes.Related (Related to)
      - System.LinkTypes.Dependency-Forward (Predecessor)
      - System.LinkTypes.Dependency-Reverse (Successor)
    - comment (optional): Comment for the link

    Backward Compatible:
    - Also accepts legacy parameters: parentId, childIds (maps to Hierarchy-Forward)
    """
    logger.info("Received link_work_items request")

    try:
        # Parse request body
        req_body = req.get_json()
        logger.info(f"Request body: {json.dumps(req_body, indent=2)}")

        # Extract parameters (with backward compatibility)
        project = req_body.get("project")

        # New parameters
        source_id = req_body.get("sourceId")
        target_ids = req_body.get("targetIds")
        link_type = req_body.get("linkType", "System.LinkTypes.Hierarchy-Forward")
        comment = req_body.get("comment")

        # Legacy parameters (backward compatibility)
        if not source_id and req_body.get("parentId"):
            source_id = req_body.get("parentId")
            target_ids = req_body.get("childIds")
            link_type = "System.LinkTypes.Hierarchy-Forward"
            logger.info("Using legacy parameters (parentId/childIds) - mapping to Hierarchy-Forward")

        # Validate required fields
        if not project or not source_id or not target_ids:
            return func.HttpResponse(
                json.dumps({
                    "error": "Missing required fields: project, sourceId, targetIds (or legacy: parentId, childIds)"
                }),
                status_code=400,
                mimetype="application/json"
            )

        # Get DevOps client and link work items
        client = get_devops_client()
        result = client.link_work_items(project, source_id, target_ids, link_type, comment)

        # Return success response
        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "source_id": source_id,
                "link_type": link_type,
                "linked_items": [r for r in result["results"] if r["success"]],
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


@app.route(route="read_work_item", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
def read_work_item(req: func.HttpRequest) -> func.HttpResponse:
    """
    Read a single work item with full details from Azure DevOps
    """
    logger.info("Received read_work_item request")

    try:
        req_body = req.get_json()
        project = req_body.get("project")
        work_item_id = req_body.get("workItemId")

        if not project or not work_item_id:
            return func.HttpResponse(
                json.dumps({"error": "Missing required fields: project, workItemId"}),
                status_code=400,
                mimetype="application/json"
            )

        client = get_devops_client()
        url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitems/{work_item_id}?$expand=all&api-version={client.api_version}"

        response = requests.get(url, headers=client.headers)
        response.raise_for_status()
        work_item = response.json()

        logger.info(f"Retrieved work item #{work_item_id} from '{project}'")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "work_item": work_item
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error reading work item: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="read_work_items", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
def read_work_items(req: func.HttpRequest) -> func.HttpResponse:
    """
    Read multiple work items with flexible filtering options

    Parameters:
    - project (required): Project name or ID
    - workItemIds (optional): Array of specific work item IDs to retrieve
    - parentId (optional): Get children of this parent work item
    - depth (optional): Recursive depth for children (default: 1, max: 5)
      - depth=1: Direct children only (Epic -> Features)
      - depth=2: Children and grandchildren (Epic -> Features -> Stories)
      - depth=3+: Continue recursion
    - workItemType (optional): Filter by type (Epic, Feature, User Story, etc.)
    - state (optional): Filter by state (New, Active, Resolved, Closed)
    - top (optional): Limit results (default: 100, max: 200)

    Examples:
    - Get specific IDs: {"project": "ClientA", "workItemIds": [123, 456, 789]}
    - Get children: {"project": "ClientA", "parentId": 123}
    - Get nested hierarchy: {"project": "ClientA", "parentId": 123, "depth": 3}
    - Filter by type: {"project": "ClientA", "parentId": 123, "workItemType": "Feature"}
    """
    logger.info("Received read_work_items request")

    try:
        req_body = req.get_json()
        project = req_body.get("project")
        work_item_ids = req_body.get("workItemIds", [])
        parent_id = req_body.get("parentId")
        depth = req_body.get("depth", 1)
        work_item_type = req_body.get("workItemType")
        state = req_body.get("state")
        top = min(req_body.get("top", 100), 200)  # Max 200 results

        if not project:
            return func.HttpResponse(
                json.dumps({"error": "Missing required parameter: project"}),
                status_code=400,
                mimetype="application/json"
            )

        # Validate depth
        if depth < 1 or depth > 5:
            return func.HttpResponse(
                json.dumps({"error": "Depth must be between 1 and 5"}),
                status_code=400,
                mimetype="application/json"
            )

        client = get_devops_client()
        results = []

        # Case 1: Get specific work item IDs
        if work_item_ids:
            ids_string = ",".join(map(str, work_item_ids[:top]))
            url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitems?ids={ids_string}&$expand=relations&api-version={client.api_version}"
            response = requests.get(url, headers=client.headers)
            response.raise_for_status()
            results = response.json().get("value", [])

        # Case 2: Get children of parent (with recursive depth)
        elif parent_id:
            def get_children_recursive(item_id: int, current_depth: int, max_depth: int) -> List[Dict[str, Any]]:
                """Recursively get children up to specified depth"""
                if current_depth > max_depth:
                    return []

                # Get parent with relations
                url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitems/{item_id}?$expand=relations&api-version={client.api_version}"
                response = requests.get(url, headers=client.headers)
                response.raise_for_status()
                parent_item = response.json()

                children = []
                child_ids = []

                # Extract direct child IDs
                if "relations" in parent_item:
                    for relation in parent_item["relations"]:
                        if relation["rel"] == "System.LinkTypes.Hierarchy-Forward":
                            child_url = relation["url"]
                            child_id = int(child_url.split("/")[-1])
                            child_ids.append(child_id)

                # Get full details for each direct child
                for child_id in child_ids:
                    child_url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitems/{child_id}?$expand=relations&api-version={client.api_version}"
                    child_response = requests.get(child_url, headers=client.headers)
                    child_response.raise_for_status()
                    child_item = child_response.json()

                    # Apply filters
                    if work_item_type and child_item["fields"].get("System.WorkItemType") != work_item_type:
                        continue
                    if state and child_item["fields"].get("System.State") != state:
                        continue

                    # Add depth level to item
                    child_item["_depth"] = current_depth
                    children.append(child_item)

                    # Recursively get children of this child
                    if current_depth < max_depth:
                        grandchildren = get_children_recursive(child_id, current_depth + 1, max_depth)
                        children.extend(grandchildren)

                return children

            results = get_children_recursive(parent_id, 1, depth)

        else:
            return func.HttpResponse(
                json.dumps({"error": "Must provide either 'workItemIds' or 'parentId'"}),
                status_code=400,
                mimetype="application/json"
            )

        # Apply filters to results (if not already filtered)
        if work_item_ids and (work_item_type or state):
            filtered_results = []
            for item in results:
                if work_item_type and item["fields"].get("System.WorkItemType") != work_item_type:
                    continue
                if state and item["fields"].get("System.State") != state:
                    continue
                filtered_results.append(item)
            results = filtered_results

        # Limit results
        results = results[:top]

        logger.info(f"Retrieved {len(results)} work items from '{project}' (parent={parent_id}, depth={depth})")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "count": len(results),
                "filters": {
                    "parentId": parent_id,
                    "depth": depth if parent_id else None,
                    "workItemType": work_item_type,
                    "state": state
                },
                "workItems": results
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error reading work items: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="read_projects", methods=["GET"], auth_level=func.AuthLevel.ANONYMOUS)
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


@app.route(route="read_teams", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
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


@app.route(route="read_work_item_types", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
def read_work_item_types(req: func.HttpRequest) -> func.HttpResponse:
    """
    Get available work item types for a project
    Useful for Pennie to discover what types are available (Epic, Feature, User Story, etc.)
    """
    try:
        req_body = req.get_json()
        project = req_body.get("project")

        if not project:
            return func.HttpResponse(
                json.dumps({"error": "Missing required parameter: project"}),
                status_code=400,
                mimetype="application/json"
            )

        client = get_devops_client()

        # Get work item types for project
        url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitemtypes?api-version={client.api_version}"
        response = requests.get(url, headers=client.headers)
        response.raise_for_status()
        data = response.json()

        # Extract type information
        work_item_types = []
        for wit_type in data.get("value", []):
            work_item_types.append({
                "name": wit_type["name"],
                "description": wit_type.get("description", ""),
                "icon": wit_type.get("icon", {}).get("id", ""),
                "color": wit_type.get("color", ""),
                "isDisabled": wit_type.get("isDisabled", False)
            })

        logger.info(f"Retrieved {len(work_item_types)} work item types from '{project}'")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "count": len(work_item_types),
                "workItemTypes": work_item_types
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error reading work item types: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="search_work_items", methods=["POST"], auth_level=func.AuthLevel.ANONYMOUS)
def search_work_items(req: func.HttpRequest) -> func.HttpResponse:
    """
    Search work items with flexible filtering
    Supports filters: workItemType, state, assignedTo, tags, title (contains)
    """
    try:
        req_body = req.get_json()
        project = req_body.get("project")
        work_item_type = req_body.get("workItemType")  # Optional: Epic, Feature, User Story, etc.
        state = req_body.get("state")  # Optional: New, Active, Resolved, Closed
        assigned_to = req_body.get("assignedTo")  # Optional: user name or email
        tags = req_body.get("tags")  # Optional: comma-separated tags
        title_contains = req_body.get("titleContains")  # Optional: search in title
        top = req_body.get("top", 50)  # Default limit 50 results

        if not project:
            return func.HttpResponse(
                json.dumps({"error": "Missing required parameter: project"}),
                status_code=400,
                mimetype="application/json"
            )

        client = get_devops_client()

        # Build WIQL query
        conditions = []
        conditions.append(f"[System.TeamProject] = '{project}'")

        if work_item_type:
            conditions.append(f"[System.WorkItemType] = '{work_item_type}'")

        if state:
            conditions.append(f"[System.State] = '{state}'")

        if assigned_to:
            conditions.append(f"[System.AssignedTo] = '{assigned_to}'")

        if tags:
            conditions.append(f"[System.Tags] CONTAINS '{tags}'")

        if title_contains:
            conditions.append(f"[System.Title] CONTAINS '{title_contains}'")

        wiql = f"SELECT [System.Id] FROM WorkItems WHERE {' AND '.join(conditions)} ORDER BY [System.ChangedDate] DESC"

        # Execute WIQL query (needs application/json, not application/json-patch+json)
        url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/wiql?api-version={client.api_version}"
        query_payload = {"query": wiql}
        wiql_headers = {**client.headers, "Content-Type": "application/json"}
        response = requests.post(url, headers=wiql_headers, json=query_payload)
        response.raise_for_status()
        query_result = response.json()

        # Get work item IDs
        work_item_ids = [item["id"] for item in query_result.get("workItems", [])][:top]

        if not work_item_ids:
            return func.HttpResponse(
                json.dumps({
                    "success": True,
                    "project": project,
                    "count": 0,
                    "workItems": [],
                    "filters": {
                        "workItemType": work_item_type,
                        "state": state,
                        "assignedTo": assigned_to,
                        "tags": tags,
                        "titleContains": title_contains
                    }
                }),
                status_code=200,
                mimetype="application/json"
            )

        # Get full work item details
        ids_string = ",".join(map(str, work_item_ids))
        details_url = f"https://dev.azure.com/{client.organization}/{project}/_apis/wit/workitems?ids={ids_string}&api-version={client.api_version}"
        details_response = requests.get(details_url, headers=client.headers)
        details_response.raise_for_status()
        work_items = details_response.json().get("value", [])

        logger.info(f"Found {len(work_items)} work items matching filters in '{project}'")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "project": project,
                "count": len(work_items),
                "workItems": work_items,
                "filters": {
                    "workItemType": work_item_type,
                    "state": state,
                    "assignedTo": assigned_to,
                    "tags": tags,
                    "titleContains": title_contains
                }
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error searching work items: {str(e)}", exc_info=True)
        return func.HttpResponse(
            json.dumps({"error": f"Internal server error: {str(e)}"}),
            status_code=500,
            mimetype="application/json"
        )


@app.route(route="read_link_types", methods=["GET"], auth_level=func.AuthLevel.ANONYMOUS)
def read_link_types(req: func.HttpRequest) -> func.HttpResponse:
    """
    Get available work item link types
    Returns common Azure DevOps link types that Pennie can use
    """
    try:
        # Return standard Azure DevOps link types
        link_types = [
            {
                "name": "System.LinkTypes.Hierarchy-Forward",
                "displayName": "Child",
                "description": "Parent → Child relationship (Epic → Feature → Story)",
                "direction": "forward",
                "category": "hierarchy"
            },
            {
                "name": "System.LinkTypes.Hierarchy-Reverse",
                "displayName": "Parent",
                "description": "Child → Parent relationship (Story → Feature → Epic)",
                "direction": "reverse",
                "category": "hierarchy"
            },
            {
                "name": "System.LinkTypes.Related",
                "displayName": "Related",
                "description": "Related work items (bidirectional)",
                "direction": "bidirectional",
                "category": "related"
            },
            {
                "name": "System.LinkTypes.Dependency-Forward",
                "displayName": "Successor",
                "description": "This work item must be completed before the linked item (Predecessor → Successor)",
                "direction": "forward",
                "category": "dependency"
            },
            {
                "name": "System.LinkTypes.Dependency-Reverse",
                "displayName": "Predecessor",
                "description": "The linked item must be completed before this work item (Successor → Predecessor)",
                "direction": "reverse",
                "category": "dependency"
            },
            {
                "name": "System.LinkTypes.Duplicate-Forward",
                "displayName": "Duplicate",
                "description": "This item is a duplicate of the linked item",
                "direction": "forward",
                "category": "other"
            },
            {
                "name": "System.LinkTypes.Duplicate-Reverse",
                "displayName": "Duplicate Of",
                "description": "The linked item is a duplicate of this item",
                "direction": "reverse",
                "category": "other"
            }
        ]

        logger.info(f"Retrieved {len(link_types)} work item link types")

        return func.HttpResponse(
            json.dumps({
                "success": True,
                "count": len(link_types),
                "linkTypes": link_types,
                "defaultLinkType": "System.LinkTypes.Hierarchy-Forward",
                "categories": {
                    "hierarchy": "Parent-child relationships for work breakdown structure",
                    "dependency": "Task dependencies and ordering",
                    "related": "General associations between work items",
                    "other": "Duplicates and special relationships"
                }
            }),
            status_code=200,
            mimetype="application/json"
        )

    except Exception as e:
        logger.error(f"Error reading link types: {str(e)}", exc_info=True)
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
