#!/bin/bash
# Deploys Pennie the Prepper agent configuration using Python SDK
# This script creates a virtual environment to avoid system package conflicts

set -e

echo "🤖 Deploying Pennie the Prepper Agent Configuration"
echo ""

# Change to script directory
cd "$(dirname "$0")"
REPO_ROOT="$(cd .. && pwd)"

# Create virtual environment if it doesn't exist
if [ ! -d "$REPO_ROOT/.venv" ]; then
    echo "📦 Creating Python virtual environment..."
    python3 -m venv "$REPO_ROOT/.venv"
    echo "   ✅ Virtual environment created"
fi

# Activate virtual environment
echo "🔧 Activating virtual environment..."
source "$REPO_ROOT/.venv/bin/activate"

# Install required packages
echo "📥 Installing required Python packages..."
pip install --quiet --upgrade pip
pip install --quiet azure-ai-projects azure-ai-agents azure-identity

echo "   ✅ Packages installed"
echo ""

# Run the configuration script using the venv's python
echo "🚀 Running Pennie configuration..."
"$REPO_ROOT/.venv/bin/python" "$REPO_ROOT/scripts/configure-pennie-agent.py"

# Deactivate virtual environment
deactivate

echo ""
echo "✅ Deployment complete!"
