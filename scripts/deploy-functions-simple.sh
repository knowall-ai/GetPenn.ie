#!/bin/bash
set -e

echo "🚀 Simple Azure Functions Deployment"
echo "===================================="

# Navigate to source directory
cd "$(dirname "$0")/../src" || exit 1

# Create deployment package
echo "📦 Creating deployment package..."
rm -f deploy.zip
zip -r deploy.zip . \
  -x "*.pyc" \
  -x "*__pycache__/*" \
  -x "*.git/*" \
  -x "*.venv/*" \
  -x ".vscode/*"

echo "✅ Package created: $(ls -lh deploy.zip | awk '{print $5}')"

# Deploy to Azure
echo "🌐 Deploying to Azure Functions..."
az functionapp deployment source config-zip \
  --resource-group TMinus15Agents \
  --name pennie-backend-prod \
  --src deploy.zip \
  --build-remote true

echo "⏳ Waiting for deployment to complete..."
sleep 10

# Restart function app
echo "🔄 Restarting function app..."
az functionapp restart \
  --name pennie-backend-prod \
  --resource-group TMinus15Agents

echo "⏳ Waiting for functions to warm up..."
sleep 20

# Test health endpoint
echo "🧪 Testing health endpoint..."
HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" https://pennie-backend-prod.azurewebsites.net/api/health)

if [ "$HTTP_CODE" = "200" ]; then
  echo "✅ Functions are running! Health check passed (HTTP $HTTP_CODE)"
  echo ""
  echo "Testing read_projects..."
  curl -s https://pennie-backend-prod.azurewebsites.net/api/read_projects | jq -r '.projects[0:3] | .[] | .name' || echo "Warming up..."
else
  echo "⚠️  Health check returned HTTP $HTTP_CODE"
  echo "Functions may still be warming up. Wait 30-60 seconds and test manually:"
  echo "  curl https://pennie-backend-prod.azurewebsites.net/api/read_projects"
fi

echo ""
echo "🎉 Deployment complete!"
echo "Backend URL: https://pennie-backend-prod.azurewebsites.net"
