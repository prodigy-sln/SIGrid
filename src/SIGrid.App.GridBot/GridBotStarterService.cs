using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SIGrid.App.GridBot.OKX;

namespace SIGrid.App.GridBot;

public class GridBotStarterService : BackgroundService
{
    private readonly IOptions<SIGridOptions> _options;
    private readonly IServiceProvider _sp;
    private readonly OKXConnector _okxConnector;
    private readonly ILogger<GridBotStarterService> _log;

    public GridBotStarterService(IOptions<SIGridOptions> options, IServiceProvider sp, OKXConnector okxConnector,
        ILogger<GridBotStarterService> log)
    {
        _options = options;
        _sp = sp;
        _okxConnector = okxConnector;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _okxConnector.InitializeAsync(stoppingToken);

        var bots = _options.Value.TradedSymbols
            .Select(async symbol =>
                {
                    GridBot? bot = null;
                    try
                    {
                        _log.LogInformation("{Symbol} - Starting bot with config: {BotConfiguration}", symbol.Symbol, JsonConvert.SerializeObject(symbol, Formatting.None));
                        bot = ActivatorUtilities.CreateInstance<GridBot>(_sp, _okxConnector, symbol);
                        await bot.StartAsync(stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        _log.LogInformation("{Symbol} - Received stop signal, shutting down.", symbol.Symbol);

                        if (bot != null)
                        {
                            await bot.CancelAllPendingOrders();
                        }

                        _log.LogInformation("{Symbol} - Bot stopped.", symbol.Symbol);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error running grid bot for '{Symbol}' on '{Exchange}'", symbol.Symbol,
                            symbol.Exchange);
                    }
                }
            );

        await Task.WhenAll(bots);
    }
}
