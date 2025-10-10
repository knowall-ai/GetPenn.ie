# GitHub Actions Workflows

This directory contains CI/CD workflows for Pennie the Prepper.

## Workflows

### test.yml - Continuous Integration
**Triggers**: Pull requests to main/develop, pushes to develop

**What it does**:
- Lints code (.NET format, Python linting)
- Builds bot application
- Runs unit tests (when implemented)
- Validates Bicep templates
- Runs integration tests (on develop branch)
- Comments on PRs with test results

**No secrets required** - runs on all commits automatically.

### deploy.yml - Continuous Deployment
**Triggers**: Push to main, manual workflow dispatch

**What it does**:
- Deploys Azure infrastructure via Bicep
- Builds and publishes bot application
- Deploys bot to Windows VM
- Runs smoke tests
- Sends deployment notifications

**Requires secrets** - only runs if enabled (see below).

## Enabling Azure Deployment

The deployment workflow is **disabled by default** to prevent failures when secrets are not configured.

### Step 1: Configure GitHub Secrets

Go to **Settings → Secrets and variables → Actions** and add:

| Secret Name | Description | How to Get |
|-------------|-------------|------------|
| `AZURE_CREDENTIALS` | Service principal JSON for Azure login | See below |
| `AZURE_SUBSCRIPTION_ID` | Your Azure subscription ID | Azure Portal |
| `AZURE_RESOURCE_GROUP` | Resource group name | e.g., `TMinus15Agents` |
| `TEAMS_APP_ID` | Teams bot app ID | Azure AD App Registration |
| `TEAMS_APP_PASSWORD` | Teams bot app password | Azure AD App Registration |
| `AZURE_DEVOPS_ORG` | Azure DevOps organization | e.g., `YourOrg` |
| `AZURE_DEVOPS_PROJECT` | Azure DevOps project | e.g., `YourProject` |
| `AZURE_DEVOPS_PAT` | Personal access token | Azure DevOps → User Settings → PAT |

### Step 2: Create Service Principal for GitHub Actions

```bash
# Login to Azure
az login

# Create service principal with Contributor role
az ad sp create-for-rbac \
  --name "github-actions-pennie" \
  --role contributor \
  --scopes /subscriptions/{subscription-id} \
  --sdk-auth

# Output will be JSON - copy entire output to AZURE_CREDENTIALS secret
```

**Example output** (save entire JSON as `AZURE_CREDENTIALS`):
```json
{
  "clientId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "clientSecret": "your-secret-here",
  "subscriptionId": "your-subscription-id",
  "tenantId": "your-tenant-id",
  "activeDirectoryEndpointUrl": "https://login.microsoftonline.com",
  "resourceManagerEndpointUrl": "https://management.azure.com/",
  "activeDirectoryGraphResourceId": "https://graph.windows.net/",
  "sqlManagementEndpointUrl": "https://management.core.windows.net:8443/",
  "galleryEndpointUrl": "https://gallery.azure.com/",
  "managementEndpointUrl": "https://management.core.windows.net/"
}
```

### Step 3: Enable Deployment Workflow

Go to **Settings → Secrets and variables → Actions → Variables** and add:

| Variable Name | Value |
|---------------|-------|
| `AZURE_DEPLOYMENT_ENABLED` | `true` |

This enables the deployment jobs in the workflow.

### Step 4: Trigger Deployment

**Option A: Automatic** - Push to `main` branch

**Option B: Manual** - GitHub Actions tab → Deploy Pennie to Azure → Run workflow

## Current Workflow Status

### ✅ What Works Now (Without Secrets)
- **build-bot** job - Builds .NET application
- Test workflow runs on PRs
- Bicep validation (via test workflow)

### ⏸️ What's Disabled (Requires Secrets)
- **deploy-infrastructure** - Skipped if `AZURE_DEPLOYMENT_ENABLED != 'true'`
- **deploy-bot** - Skipped if infrastructure deployment didn't run
- **run-smoke-tests** - Skipped if bot deployment didn't run
- **notify-deployment** - Skipped if smoke tests didn't run

## Package Version Issues

### Graph Communications SDK

The bot project references Microsoft Graph Communications packages, but they're **commented out** because:

1. Latest available version is `1.2.0.15382` (not `1.4.0`)
2. These packages are only needed for full Teams Media Bot implementation
3. Commenting them out allows the build to succeed for basic testing

**When to uncomment**:
- When implementing full Graph Communications Media Bot
- Update version to `1.2.0.15382` or latest available

**Current state**:
```xml
<!-- Commented out until needed for full implementation -->
<!-- <PackageReference Include="Microsoft.Graph.Communications.Calls" Version="1.2.0.15382" /> -->
```

## Troubleshooting

### Build Fails: "Unable to find package"

**Symptom**: NuGet restore fails for Microsoft.Graph.Communications packages

**Solution**: Packages are commented out in `bot/PennieBot.csproj` - this is intentional.

### Deploy Fails: "Login failed"

**Symptom**: Azure Login step fails with "Not all values are present"

**Solution**:
1. Verify `AZURE_CREDENTIALS` secret is configured
2. Verify JSON is complete and valid
3. Set `AZURE_DEPLOYMENT_ENABLED` variable to `true`

### Jobs Skipped: "deploy-infrastructure skipped"

**Symptom**: Deployment jobs show as skipped

**Solution**: This is expected if `AZURE_DEPLOYMENT_ENABLED` is not set to `true`. Deployments are disabled by default.

## Workflow Logs

View workflow runs: **Actions** tab → Select workflow → View logs

Failed jobs show detailed error messages.

## Security Notes

- Never commit secrets to Git
- Use GitHub encrypted secrets for sensitive data
- Service principal should have minimal required permissions
- Rotate PAT tokens every 90 days
- Use Key Vault references in production

## Next Steps

1. ✅ Configure GitHub secrets (see Step 1 above)
2. ✅ Enable deployment workflow (set `AZURE_DEPLOYMENT_ENABLED=true`)
3. ✅ Test infrastructure deployment manually first
4. ✅ Enable automatic deployments on main branch
