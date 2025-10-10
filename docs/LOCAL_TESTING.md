# Local Testing Guide

This guide explains how to test Pennie components locally during development.

## What Can Be Tested Locally

### ✅ Level 1: Bot Framework Basics (Available Now)
- Bot receives messages
- Bot responds to text
- Bot joins conversations
- HTTP endpoints working
- Logging and telemetry

### ✅ Level 2: Speech Services (Requires Azure)
- Audio transcription
- Speaker diarization
- Push audio stream

### ❌ Level 3: Teams Meeting Audio (Requires Full Deployment)
- Graph Communications SDK (Windows Server required)
- Real-time audio capture
- RTP stream processing

## Local Testing Setup

### Prerequisites

**Required**:
- Windows 10/11 or Windows Server (for Graph SDK)
- .NET 8.0 SDK
- Visual Studio 2022 or VS Code with C# extension
- [Bot Framework Emulator](https://github.com/Microsoft/BotFramework-Emulator/releases)

**Optional (for full testing)**:
- Azure subscription (for Speech Services)
- Azure DevOps account (for MCP server)
- Node.js 20+ (for MCP server)

### 1. Basic Bot Testing (No Azure Required)

This tests the Bot Framework integration without any cloud services.

#### Step 1: Configure Local Settings

Create `bot/appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  },
  "MicrosoftAppId": "",
  "MicrosoftAppPassword": "",
  "AZURE_SPEECH_KEY": "PLACEHOLDER",
  "AZURE_LOCATION": "uksouth",
  "PENNIE_AGENT_ENDPOINT": ""
}
```

**Note**: Empty `MicrosoftAppId` and `MicrosoftAppPassword` work for local emulator testing.

#### Step 2: Build and Run

```bash
cd bot
dotnet restore
dotnet build
dotnet run
```

Expected output:
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:5001
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
```

#### Step 3: Test with Bot Framework Emulator

1. **Download and install** [Bot Framework Emulator](https://github.com/Microsoft/BotFramework-Emulator/releases)

2. **Open Emulator** and click "Open Bot"

3. **Enter Bot URL**: `http://localhost:5000/api/messages`

4. **Leave credentials empty** for local testing

5. **Send a test message**: Type "Hello Pennie"

6. **Expected response**: "Echo: Hello Pennie"

#### What This Tests:
- ✅ Bot Framework adapter working
- ✅ HTTP endpoint responding
- ✅ Message processing pipeline
- ✅ Dependency injection
- ✅ Logging

#### Limitations:
- ❌ No Teams-specific features (meeting join, audio)
- ❌ No Azure Speech Services
- ❌ No AI agent integration

---

### 2. Speech Services Testing (Requires Azure)

This tests Azure Speech Services integration with pre-recorded audio.

#### Prerequisites:
- Azure Speech Services resource created
- Speech Services API key

#### Step 1: Configure Azure Credentials

Update `appsettings.Development.json`:

```json
{
  "AZURE_SPEECH_KEY": "your-speech-services-key",
  "AZURE_LOCATION": "uksouth"
}
```

**OR** use environment variables:

```bash
# Windows PowerShell
$env:AZURE_SPEECH_KEY="your-key-here"
$env:AZURE_LOCATION="uksouth"

# Linux/Mac
export AZURE_SPEECH_KEY="your-key-here"
export AZURE_LOCATION="uksouth"
```

#### Step 2: Create Test Script

Create `bot/TestSpeech.cs`:

```csharp
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

public class SpeechTest
{
    public static async Task TestTranscription()
    {
        var speechKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var speechRegion = Environment.GetEnvironmentVariable("AZURE_LOCATION") ?? "uksouth";

        var config = SpeechConfig.FromSubscription(speechKey, speechRegion);
        config.SpeechRecognitionLanguage = "en-US";

        // Use microphone input
        using var audioConfig = AudioConfig.FromDefaultMicrophoneInput();
        using var recognizer = new SpeechRecognizer(config, audioConfig);

        Console.WriteLine("Speak into your microphone...");

        var result = await recognizer.RecognizeOnceAsync();

        if (result.Reason == ResultReason.RecognizedSpeech)
        {
            Console.WriteLine($"✅ Recognized: {result.Text}");
        }
        else
        {
            Console.WriteLine($"❌ Error: {result.Reason}");
        }
    }
}
```

#### Step 3: Run Test

```bash
dotnet run --project bot/PennieBot.csproj
# In bot code, call SpeechTest.TestTranscription()
```

#### What This Tests:
- ✅ Azure Speech Services connectivity
- ✅ API key authentication
- ✅ Real-time speech recognition
- ⚠️ Speaker diarization (requires MeetingTranscriber with multi-speaker audio)

---

### 3. Mock Teams Meeting Simulation

Since Graph Communications SDK requires Windows Server in production, we can simulate the flow locally.

#### Create Mock Audio Stream

Create `bot/Testing/MockMeetingSimulator.cs`:

```csharp
using PennieBot.Services;

public class MockMeetingSimulator
{
    private readonly ISpeechTranscriptionService _speechService;
    private readonly IPennieAgentClient _agentClient;

    public MockMeetingSimulator(
        ISpeechTranscriptionService speechService,
        IPennieAgentClient agentClient)
    {
        _speechService = speechService;
        _agentClient = agentClient;
    }

    public async Task SimulateMeeting()
    {
        Console.WriteLine("🎙️ Simulating Teams meeting...");

        var meetingId = "test-meeting-001";

        // Start transcription (will use microphone or audio file)
        await _speechService.StartTranscriptionAsync(
            meetingId,
            async (result) =>
            {
                Console.WriteLine($"📝 Transcribed: {result.Speaker} - {result.Text}");

                // Forward to Pennie agent
                await _agentClient.SendTranscriptAsync(result);
            });

        Console.WriteLine("✅ Meeting started. Speak into your microphone...");
        Console.WriteLine("Press any key to stop.");
        Console.ReadKey();

        await _speechService.StopTranscriptionAsync(meetingId);
        Console.WriteLine("🛑 Meeting stopped.");
    }
}
```

#### Run Simulation

```bash
dotnet run
# Call MockMeetingSimulator.SimulateMeeting() from Program.cs
```

#### What This Tests:
- ✅ Speech transcription flow
- ✅ Callback mechanism
- ✅ Agent client integration
- ❌ Doesn't test actual Teams meeting join
- ❌ Doesn't test RTP audio streams

---

### 4. MCP Server Testing (Azure DevOps)

Test the Azure DevOps MCP Server locally.

#### Prerequisites:
- Node.js 20+ installed
- Azure DevOps organization and project
- Personal Access Token (PAT) with work items read/write

#### Step 1: Install MCP Server

```bash
npm install -g @azure-devops/mcp
```

#### Step 2: Set Environment Variables

```bash
# Windows
$env:AZURE_DEVOPS_ORG="YourOrg"
$env:AZURE_DEVOPS_PROJECT="YourProject"

# Linux/Mac
export AZURE_DEVOPS_ORG="YourOrg"
export AZURE_DEVOPS_PROJECT="YourProject"
```

#### Step 3: Authenticate

```bash
npx @azure-devops/mcp YourOrg
```

This opens a browser for OAuth authentication.

#### Step 4: Test Work Item Creation

```bash
# List available tools
npx @azure-devops/mcp YourOrg --list-tools

# Test connection
npx @azure-devops/mcp YourOrg --test
```

#### What This Tests:
- ✅ MCP server installation
- ✅ Azure DevOps authentication
- ✅ Work item API access
- ❌ Doesn't test integration with Pennie agent

---

## Testing Fixtures

Use the provided test fixtures to validate behavior:

### Test Transcript Processing

```bash
# Read test transcript
cat tests/fixtures/transcripts/happy_path/customer-portal-epic.txt

# Expected output
# Epic: Customer Portal with SSO Integration
# Features: OAuth 2.0, SAML, Password Reset, Audit Logging
```

### Unit Test Structure (To Be Implemented)

```csharp
// tests/PennieBot.Tests/SpeechTranscriptionServiceTests.cs
[Fact]
public async Task ProcessAudioAsync_ShouldInvokeCallback()
{
    // Arrange
    var mockLogger = new Mock<ILogger<SpeechTranscriptionService>>();
    var mockConfig = new Mock<IConfiguration>();
    var service = new SpeechTranscriptionService(mockLogger.Object, mockConfig.Object);

    // Act
    await service.ProcessAudioAsync("meeting-001", audioData);

    // Assert
    // Verify callback was invoked
}
```

---

## Common Issues and Solutions

### Issue 1: Bot Framework Emulator Can't Connect

**Symptoms**: Emulator shows "Unable to connect to bot"

**Solutions**:
1. Check bot is running: `netstat -ano | findstr :5000`
2. Use HTTP (not HTTPS): `http://localhost:5000/api/messages`
3. Leave credentials empty for local testing
4. Check Windows Firewall allows localhost connections

### Issue 2: Speech Services Authentication Fails

**Symptoms**: "401 Unauthorized" from Speech Services

**Solutions**:
1. Verify API key is correct: Check Azure Portal
2. Check region matches: `AZURE_LOCATION=uksouth`
3. Test key with curl:
   ```bash
   curl -X POST "https://uksouth.api.cognitive.microsoft.com/sts/v1.0/issueToken" \
     -H "Ocp-Apim-Subscription-Key: YOUR_KEY"
   ```

### Issue 3: MCP Server Authentication Loop

**Symptoms**: Browser keeps opening for authentication

**Solutions**:
1. Clear cached credentials:
   ```bash
   Remove-Item -Path "$env:USERPROFILE\.azure-devops-mcp" -Recurse -Force
   ```
2. Re-authenticate with correct account
3. Ensure account has project access

### Issue 4: No Audio from Microphone

**Symptoms**: Speech recognition times out

**Solutions**:
1. Check microphone permissions (Windows Settings → Privacy → Microphone)
2. Test microphone: Windows Sound Settings → Recording
3. Try different microphone device
4. Check audio format (16kHz, mono, 16-bit PCM)

---

## Next Steps After Local Testing

Once local components are tested:

1. **Deploy Infrastructure**: Use Bicep templates to deploy to Azure
2. **Configure Teams Bot**: Create app registration and grant permissions
3. **Deploy to Windows VM**: Install bot as Windows Service
4. **Test in Real Meeting**: Join actual Teams meeting

---

## Limitations of Local Testing

**Cannot Test Locally**:
- ❌ Graph Communications Media SDK (requires Windows Server + production environment)
- ❌ Actual Teams meeting join/leave events
- ❌ RTP audio stream processing (50 frames/sec)
- ❌ Teams-specific features (meeting roster, participant events)
- ❌ Production-grade speaker diarization (requires multi-speaker audio)

**These require full Azure deployment and real Teams environment.**

---

## Quick Start: Minimal Local Test

For fastest local validation:

```bash
# 1. Clone repo
git clone https://github.com/knowall-ai/GetPenn.ie.git
cd GetPenn.ie/bot

# 2. Build
dotnet restore
dotnet build

# 3. Run
dotnet run

# 4. Test with Bot Framework Emulator
# Open emulator → Connect to http://localhost:5000/api/messages
# Send message: "Hello Pennie"
# Expect: "Echo: Hello Pennie"
```

**That's it!** This confirms basic Bot Framework integration is working.
