# Pennie Bot - Teams Media Bot

This directory contains the C# .NET application for Pennie's Teams Media Bot, which handles:

- Joining Microsoft Teams meetings via Graph Communications SDK
- Capturing real-time audio (RTP streams)
- Sending audio to Azure Speech Services for transcription with speaker diarization
- Forwarding transcripts to Pennie AI Foundry Agent

## Project Structure

```
bot/
├── Bots/
│   └── MediaBot.cs                # Main bot logic
├── Controllers/
│   └── BotController.cs           # HTTP endpoint for Bot Framework
├── Services/
│   ├── ISpeechTranscriptionService.cs   # Speech Services interface
│   ├── SpeechTranscriptionService.cs    # Speech Services implementation
│   ├── IPennieAgentClient.cs            # Pennie agent interface
│   └── PennieAgentClient.cs             # Pennie agent client
├── Program.cs                     # Application entry point
├── AdapterWithErrorHandler.cs     # Bot Framework adapter with error handling
├── PennieBot.csproj              # Project file
├── appsettings.json              # Configuration
└── appsettings.Development.json  # Development configuration
```

## Prerequisites

- .NET 8.0 SDK
- Windows Server (required for Graph Communications Media SDK)
- Azure Bot Service registration
- Azure Speech Services resource
- Application Insights for telemetry

## Configuration

### Environment Variables

Set these in Azure Key Vault or Windows environment variables:

| Variable | Description |
|----------|-------------|
| `MicrosoftAppId` | Teams bot app ID (from Azure AD app registration) |
| `MicrosoftAppPassword` | Teams bot app password |
| `AZURE_SPEECH_KEY` | Azure Speech Services API key |
| `AZURE_LOCATION` | Azure region (e.g., `uksouth`) |
| `PENNIE_AGENT_ENDPOINT` | Pennie AI Foundry Agent endpoint URL |
| `AZURE_KEY_VAULT_NAME` | Azure Key Vault name for secrets |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Application Insights connection string |

### appsettings.json

Copy `appsettings.json` and update with your values, or use environment variables.

## Build

```bash
dotnet restore
dotnet build --configuration Release
```

## Run Locally (Development)

```bash
dotnet run --project PennieBot.csproj
```

The bot will start on `https://localhost:5001` by default.

### Test with Bot Framework Emulator

1. Download [Bot Framework Emulator](https://github.com/Microsoft/BotFramework-Emulator/releases)
2. Open Emulator and connect to `http://localhost:5000/api/messages`
3. Enter your bot app ID and password
4. Send test messages

## Deploy to Windows Server VM

### Option 1: Publish and Copy

```bash
# Publish for Windows x64
dotnet publish --configuration Release --runtime win-x64 --self-contained true

# Copy to VM
scp -r bin/Release/net8.0/win-x64/publish/* admin@<vm-ip>:C:\Pennie\bot\
```

### Option 2: GitHub Actions (Automated)

See [.github/workflows/deploy.yml](../.github/workflows/deploy.yml) for automated deployment.

## Install as Windows Service

On the Windows VM:

```powershell
# Install NSSM (if not already installed)
choco install nssm

# Install as service
nssm install PennieBot "C:\Pennie\bot\PennieBot.exe"
nssm set PennieBot AppDirectory "C:\Pennie\bot"
nssm set PennieBot DisplayName "Pennie the Prepper - Teams Bot"
nssm set PennieBot Description "AI-powered business analyst for Teams meetings"
nssm set PennieBot Start SERVICE_AUTO_START

# Set environment variables
nssm set PennieBot AppEnvironmentExtra ^
  APPLICATIONINSIGHTS_CONNECTION_STRING=<connection-string> ^
  AZURE_KEY_VAULT_NAME=<keyvault-name>

# Start service
nssm start PennieBot

# Check status
nssm status PennieBot

# View logs
Get-Content C:\Pennie\logs\bot.log -Tail 50 -Wait
```

## Health Check

The bot exposes a health check endpoint:

```bash
curl https://<vm-ip>/health
```

Expected response: `200 OK`

## Graph Communications SDK Integration

**Note**: The current implementation contains placeholder code for Graph Communications Media Bot.

Full integration requires:

1. **Create Call**:
   ```csharp
   var call = await graphClient.Communications.Calls
       .Request()
       .AddAsync(new Call { ... });
   ```

2. **Subscribe to Audio**:
   ```csharp
   call.AudioSocket.Receive += (sender, args) =>
   {
       // Process RTP audio frames
       var audioData = args.Buffer.Data;
       await _speechService.ProcessAudioAsync(meetingId, audioData);
   };
   ```

3. **Configure Media Platform**:
   - Requires Media Platform SDK
   - Windows Server only
   - Separate media endpoint (separate from bot messaging endpoint)

See [Microsoft Graph Communications SDK documentation](https://docs.microsoft.com/en-us/graph/api/resources/communications-api-overview) for details.

## Logging

Logs are sent to:

1. **Console** (visible in service logs)
2. **Application Insights** (for production monitoring)
3. **File** (optional, configure in `appsettings.json`)

View logs:

```powershell
# Service logs
Get-Content C:\Pennie\logs\bot.log -Tail 100

# Windows Event Viewer
eventvwr.msc -> Application -> PennieBot
```

## Troubleshooting

See [docs/TROUBLESHOOTING.adoc](../docs/TROUBLESHOOTING.adoc) for common issues.

**Quick checks**:

```powershell
# Check service status
nssm status PennieBot

# Test health endpoint
Invoke-WebRequest -Uri http://localhost/health

# Check logs
Get-Content C:\Pennie\logs\bot.log -Tail 50

# Restart service
nssm restart PennieBot
```

## Testing

Unit tests to be added in `tests/` directory.

## Resources

- [Bot Framework Documentation](https://docs.microsoft.com/en-us/azure/bot-service/)
- [Graph Communications SDK](https://docs.microsoft.com/en-us/graph/api/resources/communications-api-overview)
- [Azure Speech Services SDK](https://docs.microsoft.com/en-us/azure/cognitive-services/speech-service/speech-sdk)
- [Microsoft Teams Bot Development](https://docs.microsoft.com/en-us/microsoftteams/platform/bots/what-are-bots)

## License

MIT License - see [LICENSE](../LICENSE) for details.
