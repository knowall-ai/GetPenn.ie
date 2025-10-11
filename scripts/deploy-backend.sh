#!/bin/bash

# Pennie Backend Deployment Script
# Deploys Azure Functions backend for handling Azure DevOps work item creation

set -e

echo "🚀 Deploying Pennie Backend to Azure Functions"

# Load environment variables
if [ -f .env ]; then
    export $(cat .env | grep -v '^#' | xargs)
fi

# Validate required environment variables
if [ -z "$AZURE_DEVOPS_ORG" ] || [ -z "$AZURE_DEVOPS_PROJECT" ] || [ -z "$AZURE_DEVOPS_PAT" ]; then
    echo "❌ Error: Missing required environment variables"
    echo "   Please set: AZURE_DEVOPS_ORG, AZURE_DEVOPS_PAT in .env"
    exit 1
fi

RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-TMinus15Agents}"
LOCATION="${AZURE_LOCATION:-uksouth}"
FUNCTION_APP_NAME="pennie-backend"
ENVIRONMENT_NAME="${AZURE_ENV_NAME:-prod}"

echo "📋 Deployment Configuration:"
echo "   Resource Group: $RESOURCE_GROUP"
echo "   Location: $LOCATION"
echo "   Function App: $FUNCTION_APP_NAME-$ENVIRONMENT_NAME"
echo "   DevOps Org: $AZURE_DEVOPS_ORG"
echo "   DevOps Project: $AZURE_DEVOPS_PROJECT"

# Step 1: Deploy infrastructure
echo ""
echo "📦 Step 1: Deploying Azure Function infrastructure..."
DEPLOYMENT_OUTPUT=$(az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --template-file infra/deploy-function-app.bicep \
    --parameters functionAppName="$FUNCTION_APP_NAME" \
    --parameters location="$LOCATION" \
    --parameters environmentName="$ENVIRONMENT_NAME" \
    --parameters devOpsOrg="$AZURE_DEVOPS_ORG" \
    --parameters devOpsPAT="$AZURE_DEVOPS_PAT" \
    --query 'properties.outputs' \
    --output json)

echo "✅ Infrastructure deployed"

# Extract outputs
FUNCTION_APP_FULL_NAME=$(echo $DEPLOYMENT_OUTPUT | jq -r '.functionAppName.value')
FUNCTION_APP_URL=$(echo $DEPLOYMENT_OUTPUT | jq -r '.functionAppUrl.value')
WIT_CREATE_URL=$(echo $DEPLOYMENT_OUTPUT | jq -r '.witCreateWorkItemUrl.value')
WIT_ADD_CHILD_URL=$(echo $DEPLOYMENT_OUTPUT | jq -r '.witAddChildWorkItemsUrl.value')

echo ""
echo "📝 Deployment Details:"
echo "   Function App: $FUNCTION_APP_FULL_NAME"
echo "   URL: $FUNCTION_APP_URL"
echo "   Create Work Item: $WIT_CREATE_URL"
echo "   Add Child Items: $WIT_ADD_CHILD_URL"

# Step 2: Deploy function code
echo ""
echo "📤 Step 2: Deploying function code..."

# Create a deployment package
echo "   Creating deployment package..."
cd "$(dirname "$0")/.."
zip -r function-deploy.zip src/ requirements.txt host.json .funcignore -x "*.pyc" -x "*/__pycache__/*"

# Deploy to Azure
echo "   Uploading to Azure..."
az functionapp deployment source config-zip \
    --resource-group "$RESOURCE_GROUP" \
    --name "$FUNCTION_APP_FULL_NAME" \
    --src function-deploy.zip

# Clean up
rm function-deploy.zip

echo "✅ Function code deployed"

# Step 3: Get Function Key
echo ""
echo "🔑 Step 3: Retrieving function key..."
sleep 10  # Wait for deployment to complete

FUNCTION_KEY=$(az functionapp keys list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$FUNCTION_APP_FULL_NAME" \
    --query 'functionKeys.default' \
    --output tsv)

if [ -z "$FUNCTION_KEY" ]; then
    echo "⚠️  Warning: Could not retrieve function key automatically"
    echo "   You can get it from: Azure Portal > $FUNCTION_APP_FULL_NAME > Functions > App keys"
else
    echo "✅ Function key retrieved"
fi

# Step 4: Update .env file
echo ""
echo "💾 Step 4: Updating .env file..."

# Backup .env
cp .env .env.backup

# Add/update function URLs in .env
{
    grep -v "PENNIE_BACKEND_" .env.backup || true
    echo ""
    echo "# Pennie Backend Function URLs"
    echo "PENNIE_BACKEND_URL=$FUNCTION_APP_URL"
    echo "PENNIE_BACKEND_WIT_CREATE_URL=$WIT_CREATE_URL"
    echo "PENNIE_BACKEND_WIT_ADD_CHILD_URL=$WIT_ADD_CHILD_URL"
    if [ ! -z "$FUNCTION_KEY" ]; then
        echo "PENNIE_BACKEND_FUNCTION_KEY=$FUNCTION_KEY"
    fi
} > .env

echo "✅ .env file updated"

# Step 5: Test health endpoint
echo ""
echo "🏥 Step 5: Testing health endpoint..."
HEALTH_URL="$FUNCTION_APP_URL/api/health"
HEALTH_RESPONSE=$(curl -s "$HEALTH_URL" || echo '{"status":"unavailable"}')
HEALTH_STATUS=$(echo "$HEALTH_RESPONSE" | jq -r '.status')

if [ "$HEALTH_STATUS" == "healthy" ]; then
    echo "✅ Health check passed"
else
    echo "⚠️  Warning: Health check returned: $HEALTH_STATUS"
    echo "   The function may still be starting up. Wait a few minutes and try:"
    echo "   curl $HEALTH_URL"
fi

# Summary
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "🎉 Pennie Backend Deployed Successfully!"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "📍 Function Endpoints:"
echo "   Create Work Item: $WIT_CREATE_URL"
echo "   Add Child Items:  $WIT_ADD_CHILD_URL"
echo ""
if [ ! -z "$FUNCTION_KEY" ]; then
    echo "🔑 Authentication:"
    echo "   Add header: x-functions-key: $FUNCTION_KEY"
    echo ""
fi
echo "📝 Next Steps:"
echo "   1. Test the endpoints with curl or Postman"
echo "   2. Update Pennie agent configuration with these URLs"
echo "   3. Test end-to-end with a sample meeting transcript"
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
