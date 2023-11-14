﻿using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SIGrid.Exchange.Shared;
using SIGrid.TradeBot;

namespace SIGrid.TradeBotApp;

public class GridBotStarterService : BackgroundService
{
    private readonly OKXExchangeConnector _okxConnector;
    private readonly IOptions<SIGridOptions> _options;
    private readonly IServiceProvider _sp;
    private readonly ILogger<GridBotStarterService> _log;

    public GridBotStarterService(OKXExchangeConnector okxConnector, IOptions<SIGridOptions> options, IServiceProvider sp, ILogger<GridBotStarterService> log)
    {
        _okxConnector = okxConnector;
        _options = options;
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _okxConnector.InitializeAsync(stoppingToken);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var bots = _options.Value.TradedSymbols
            .Select(async symbol =>
                {
                    try
                    {
                        _log.LogInformation("{Symbol} - Starting bot with config: {BotConfiguration}", symbol.Symbol, JsonConvert.SerializeObject(symbol, Formatting.None));
                        await ActivatorUtilities.CreateInstance<GridBot>(_sp, symbol).StartAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Error running grid bot for '{Symbol}' on '{Exchange}'. Shutting down application.", symbol.Symbol, symbol.Exchange);
                        cts.Cancel();
                    }
                }
            );

        await Task.WhenAll(bots);
    }
}
