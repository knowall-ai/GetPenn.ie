using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Builder.TraceExtensions;
using Microsoft.Bot.Connector.Authentication;

namespace PennieBot;

public class AdapterWithErrorHandler : CloudAdapter
{
    public AdapterWithErrorHandler(
        BotFrameworkAuthentication auth,
        ILogger<AdapterWithErrorHandler> logger)
        : base(auth, logger)
    {
        OnTurnError = async (turnContext, exception) =>
        {
            // Log any leaked exception from the application.
            logger.LogError(exception, "Exception caught : {ExceptionMessage}", exception.Message);

            // Send a catch-all apology to the user.
            await turnContext.SendActivityAsync("Sorry, it looks like something went wrong.");

            // Send a trace activity for Bot Framework Emulator
            await turnContext.TraceActivityAsync(
                "OnTurnError Trace",
                exception.Message,
                "https://www.botframework.com/schemas/error",
                "TurnError");
        };
    }
}
