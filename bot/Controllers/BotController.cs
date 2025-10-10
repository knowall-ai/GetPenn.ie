using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace PennieBot.Controllers;

[Route("api/messages")]
[ApiController]
public class BotController : ControllerBase
{
    private readonly IBotFrameworkHttpAdapter _adapter;
    private readonly IBot _bot;
    private readonly ILogger<BotController> _logger;

    public BotController(
        IBotFrameworkHttpAdapter adapter,
        IBot bot,
        ILogger<BotController> logger)
    {
        _adapter = adapter;
        _bot = bot;
        _logger = logger;
    }

    [HttpPost]
    [HttpGet] // For bot framework verification
    public async Task PostAsync()
    {
        _logger.LogInformation("Received request from Bot Framework");

        try
        {
            // Delegate the processing of the HTTP POST to the adapter.
            // The adapter will invoke the bot.
            await _adapter.ProcessAsync(Request, Response, _bot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing bot request");
            throw;
        }
    }
}
