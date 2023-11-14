using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace SIGrid.App.GridBot;

public class GridBotStarterService : BackgroundService
{
    private readonly IOptions<SIGridOptions> _options;
    private readonly IServiceProvider _sp;
    private readonly OKXConnector _okxConnector;
    private readonly ILogger<GridBotStarterService> _log;

    public GridBotStarterService(IOptions<SIGridOptions> options, IServiceProvider sp, OKXConnector okxConnector, ILogger<GridBotStarterService> log)
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
                try
                {
                    _log.LogInformation("{Symbol} - Starting bot with config: {BotConfiguration}", symbol.Symbol, JsonConvert.SerializeObject(symbol, Formatting.None));
                    await ActivatorUtilities.CreateInstance<GridBot>(_sp, _okxConnector, symbol).StartAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error running grid bot for '{Symbol}' on '{Exchange}'", symbol.Symbol, symbol.Exchange);
                }
            }
        );

        await Task.WhenAll(bots);
    }
}
