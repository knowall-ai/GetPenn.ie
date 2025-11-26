using Azure.Identity;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using PennieBot;
using PennieBot.Bots;
using PennieBot.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Configuration.AddEnvironmentVariables();

// Add Key Vault if configured
// Uses Azure.Extensions.AspNetCore.Configuration.Secrets with DefaultAzureCredential
// On the VM, this uses the managed identity for authentication
var keyVaultName = builder.Configuration["AZURE_KEY_VAULT_NAME"];
if (!string.IsNullOrEmpty(keyVaultName))
{
    var keyVaultUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
    builder.Configuration.AddAzureKeyVault(keyVaultUri, new DefaultAzureCredential());
    Console.WriteLine($"Key Vault configuration loaded from: {keyVaultName}");
}

// Application Insights
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
});

// Add services
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

// Bot Framework Authentication (reads MicrosoftAppId/Password from config)
builder.Services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();

// Bot Framework Adapter
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();

// Bot Services
builder.Services.AddSingleton<IBot, MediaBot>();
builder.Services.AddSingleton<IGraphCallService, GraphCallService>();
builder.Services.AddSingleton<ISpeechTranscriptionService, SpeechTranscriptionService>();

// Only register PennieAgentClient if AI Foundry is configured
// This is optional - the bot can still handle simple queries via HTTP client
var aiFoundryEndpoint = builder.Configuration["AZURE_AI_FOUNDRY_ENDPOINT"];
if (!string.IsNullOrEmpty(aiFoundryEndpoint))
{
    builder.Services.AddSingleton<IPennieAgentClient, PennieAgentClient>();
}
else
{
    // Register a null implementation to satisfy DI
    builder.Services.AddSingleton<IPennieAgentClient>(sp =>
        new NullPennieAgentClient(sp.GetRequiredService<ILogger<NullPennieAgentClient>>()));
}

// Memory Cache (for meeting thread caching with expiration)
builder.Services.AddMemoryCache();

// HTTP Client with 30s timeout for backend calls
builder.Services.AddHttpClient<PennieAgentClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddApplicationInsights();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

// Welcome endpoint
app.MapGet("/", () => Results.Json(new
{
    name = "Pennie the Prepper Bot",
    status = "Running",
    version = "1.0.0",
    timestamp = DateTime.UtcNow
}));

app.Run();
