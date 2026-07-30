"""
Preppie Middleware - Handles function calls from Azure OpenAI Assistant
and proxies them to the Azure Functions backend
"""

import azure.functions as func
import json
import requests
import logging

logger = logging.getLogger(__name__)

# Backend URL
BACKEND_URL = "https://pennie-backend-prod.azurewebsites.net"

def handle_function_call(function_name: str, arguments: dict) -> dict:
    """
    Route function calls to the appropriate backend endpoint
    """
    logger.info(f"Handling function call: {function_name} with args: {arguments}")
    
    # Map function names to backend endpoints
    endpoint_map = {
        "read_projects": {"method": "GET", "path": "/api/read_projects"},
        "read_teams": {"method": "POST", "path": "/api/read_teams"},
        "read_work_item": {"method": "POST", "path": "/api/read_work_item"},
        "read_work_items": {"method": "POST", "path": "/api/read_work_items"},
        "read_work_item_types": {"method": "POST", "path": "/api/read_work_item_types"},
        "read_link_types": {"method": "GET", "path": "/api/read_link_types"},
        "search_work_items": {"method": "POST", "path": "/api/search_work_items"},
        "create_work_item": {"method": "POST", "path": "/api/create_work_item"},
        "link_work_items": {"method": "POST", "path": "/api/link_work_items_endpoint"},
    }
    
    if function_name not in endpoint_map:
        return {"error": f"Unknown function: {function_name}"}
    
    endpoint = endpoint_map[function_name]
    url = f"{BACKEND_URL}{endpoint['path']}"
    
    try:
        if endpoint['method'] == "GET":
            response = requests.get(url, timeout=30)
        else:
            response = requests.post(url, json=arguments, timeout=30)
        
        response.raise_for_status()
        return response.json()
        
    except requests.exceptions.RequestException as e:
        logger.error(f"Backend request failed: {e}")
        return {"error": str(e)}

# Create app instance
app = func.FunctionApp(http_auth_level=func.AuthLevel.ANONYMOUS)

@app.route(route="preppie_function_handler", methods=["POST"])
def preppie_function_handler(req: func.HttpRequest) -> func.HttpResponse:
    """
    Handle function call requests from Preppie
    
    Expected payload:
    {
        "function_name": "read_projects",
        "arguments": {}
    }
    """
    logger.info("Received function call request from Preppie")
    
    try:
        req_body = req.get_json()
        function_name = req_body.get('function_name')
        arguments = req_body.get('arguments', {})
        
        if not function_name:
            return func.HttpResponse(
                json.dumps({"error": "Missing function_name"}),
                mimetype="application/json",
                status_code=400
            )
        
        # Call backend
        result = handle_function_call(function_name, arguments)
        
        return func.HttpResponse(
            json.dumps(result),
            mimetype="application/json",
            status_code=200
        )
        
    except ValueError as e:
        logger.error(f"Invalid JSON: {e}")
        return func.HttpResponse(
            json.dumps({"error": "Invalid JSON"}),
            mimetype="application/json",
            status_code=400
        )
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        return func.HttpResponse(
            json.dumps({"error": str(e)}),
            mimetype="application/json",
            status_code=500
        )
