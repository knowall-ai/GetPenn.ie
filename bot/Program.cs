using Azure.Identity;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using PennieBot;
using PennieBot.Bots;
using PennieBot.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Configuration.AddEnvironmentVariables();

// Add Key Vault if configured
// Note: Using legacy API - update to Azure.Extensions.AspNetCore.Configuration.Secrets for newer approach
var keyVaultName = builder.Configuration["AZURE_KEY_VAULT_NAME"];
if (!string.IsNullOrEmpty(keyVaultName))
{
    // Commented out - requires newer Azure.Extensions.AspNetCore.Configuration.Secrets package
    // var keyVaultUri = new Uri($"https://{keyVaultName}.vault.azure.net/");
    // builder.Configuration.AddAzureKeyVault(keyVaultUri, new DefaultAzureCredential());

    // For now, use environment variables or appsettings.json
}

// Application Insights
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
});

// Add services
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

// Bot Framework Adapter
builder.Services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();

// Bot Services
builder.Services.AddSingleton<IBot, MediaBot>();
builder.Services.AddSingleton<IGraphCallService, GraphCallService>();
builder.Services.AddSingleton<ISpeechTranscriptionService, SpeechTranscriptionService>();
builder.Services.AddSingleton<IPennieAgentClient, PennieAgentClient>();

// HTTP Client
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
