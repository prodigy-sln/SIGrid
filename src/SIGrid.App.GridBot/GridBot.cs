using System.Collections.Concurrent;
using System.Reactive.Linq;
using CryptoExchange.Net.CommonObjects;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using OKX.Net.Objects.Market;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.App.GridBot.Extensions;
using SIGrid.App.GridBot.Grid;
using SIGrid.App.GridBot.OKX;

namespace SIGrid.App.GridBot;

public class GridBot
{
    private readonly SIGridOptions.TradedSymbolOptions _tradedSymbol;
    private readonly OKXConnector _okx;
    private readonly ILogger<GridBot> _log;
    private readonly OKXInstrumentType _symbolType;
    private readonly OKXPositionSide _positionSide = OKXPositionSide.Long;
    private readonly OKXTradeMode _tradeMode = OKXTradeMode.Cross;
    private readonly OKXOrderType _orderType = OKXOrderType.LimitOrder;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeSpan _minDelayBetweenUpdates = TimeSpan.FromSeconds(0.5);
    private readonly ConcurrentDictionary<int, byte> _pendingOrderIds = new();
    private readonly TimeSpan _pendingOrderTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _pendingOrderRemovalTaskTokens = new();
    private OKXInstrument _symbol = null!; // Initialized on start.
    private OKXFeeRate _feeRate = null!; // Initialized on start.
    private decimal _currentPrice;
    private int _currentGridLine;
    private OKXPosition? _position;
    private DateTime _lastUpdateDate = DateTime.MinValue;

    public GridBot(SIGridOptions.TradedSymbolOptions tradedSymbol, OKXConnector okxConnector, ILogger<GridBot> log)
    {
        _tradedSymbol = tradedSymbol;
        _okx = okxConnector;
        _log = log;
        _symbolType = Enum.Parse<OKXInstrumentType>(_tradedSymbol.SymbolType);
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("{Symbol} - Starting Up Bot: {BotConfig}", _tradedSymbol.Symbol, JsonConvert.SerializeObject(_tradedSymbol, Formatting.None));
        
        await LoadRequiredInformationAsync(stoppingToken);
        await SubscribeToExchangeUpdates(stoppingToken);
    }

    private void UpdateCurrentPrice(decimal? newPrice)
    {
        _currentPrice = newPrice ?? _currentPrice;
        var gridLine = GetGridLineForPrice(_currentPrice);
        if (gridLine > _currentGridLine && gridLine != 0)
        {
            UpdateCurrentGridLine(gridLine);
        }
    }

    private void UpdateCurrentGridLine(int newGridLine)
    {
        _currentGridLine = newGridLine;
        _log.LogDebug("{Symbol} - Current Grid Line: {OrderId}", _tradedSymbol.Symbol, newGridLine);
    }

    private int GetGridLineForPrice(decimal currentPrice)
    {
        return GridCalculator.GetPreviousGridLineIndex(currentPrice, _tradedSymbol.TakeProfitPercent);
    }

    private async Task LoadRequiredInformationAsync(CancellationToken ct)
    {
        var symbol = _okx.GetSymbol(_tradedSymbol.SymbolType, _tradedSymbol.Symbol);
        _symbol = symbol ?? throw new Exception($"The symbol '{_tradedSymbol.Symbol}' is not present on the exchange.");

        var feeRate = await _okx.GetFeeRateAsync(_symbol);

        _feeRate = feeRate ?? throw new Exception($"Unable to retrieve fee rate info for '{_tradedSymbol.Symbol}'.");
        UpdateCurrentPrice(await _okx.GetCurrentPriceAsync(_symbol, ct));
        UpdateCurrentGridLine(GetGridLineForPrice(_currentPrice));
    }

    private async Task SubscribeToExchangeUpdates(CancellationToken ct)
    {
        var symbolTicker = _okx.GetSymbolTickerSubscription(_symbol, ct)
            .Sample(_minDelayBetweenUpdates)
            .ToAsyncEnumerable();

        var positionUpdates = _okx.GetPositionUpdateSubscription(_symbol)
            .Sample(_minDelayBetweenUpdates)
            .ToAsyncEnumerable();

        var orderUpdates = _okx.GetOrderUpdateSubscription(_symbol)
            .ToAsyncEnumerable();

        await Task.WhenAll(
            HandleSymbolTickerUpdatesAsync(symbolTicker),
            HandlePositionUpdatesAsync(positionUpdates),
            HandleOrderUpdatesAsync(orderUpdates)
        );
    }

    private async Task HandleSymbolTickerUpdatesAsync(IAsyncEnumerable<OKXTicker> tickerSubscription)
    {
        await foreach (var ticker in tickerSubscription)
        {
            try
            {
                await HandleSymbolTickerUpdateAsync(ticker);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Symbol} - Error handling ticker update.", _tradedSymbol.Symbol);
            }
        }
    }

    private async Task HandleOrderUpdatesAsync(IAsyncEnumerable<OKXOrder> orderUpdates)
    {
        await foreach (var orderUpdate in orderUpdates)
        {
            try
            {
                _log.LogDebug("{Symbol} - Order Update Received for line {OrderId}", _tradedSymbol.Symbol, orderUpdate.GetGridLineIndex());
                await HandleOrderUpdateAsync(orderUpdate);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Symbol} - Error handling order update.", _tradedSymbol.Symbol);
            }
        }
    }

    private async Task HandlePositionUpdatesAsync(IAsyncEnumerable<OKXPosition> positionUpdates)
    {
        await foreach (var positionUpdate in positionUpdates)
        {
            try
            {
                await HandlePositionUpdateAsync(positionUpdate);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Symbol} - Error handling position update.", _tradedSymbol.Symbol);
            }
        }
    }

    private async Task HandleSymbolTickerUpdateAsync(OKXTicker ticker)
    {
        if (!ticker.LastPrice.HasValue) return;

        UpdateCurrentPrice(ticker.LastPrice);

        await HandleUpdateEventAsync();
    }

    private async Task HandleOrderUpdateAsync(OKXOrder orderUpdate)
    {
        if (orderUpdate.OrderState is OKXOrderState.Filled or OKXOrderState.PartiallyFilled)
        {
            UpdateCurrentPrice(orderUpdate.FillPrice);
        }

        if (orderUpdate.OrderState == OKXOrderState.Filled)
        {
            UpdateStateForFilledOrder(orderUpdate);
        }

        if (orderUpdate.OrderState == OKXOrderState.Live)
        {
            RemovePendingOrder(orderUpdate);
        }

        await HandleUpdateEventAsync();
    }

    private void RemovePendingOrder(OKXOrder orderUpdate)
    {
        var line = orderUpdate.GetGridLineIndex();

        _pendingOrderIds.Remove(line, out _);
        CancelPendingOrderCleanupTask(line);
    }

    private void UpdateStateForFilledOrder(OKXOrder orderUpdate)
    {
        var orderId = orderUpdate.GetGridLineIndex();
        if (orderId <= 0) return;

        UpdateCurrentGridLine(orderId);
    }

    private async Task HandlePositionUpdateAsync(OKXPosition positionUpdate)
    {
        Interlocked.Exchange(ref _position, positionUpdate);

        await HandleUpdateEventAsync();
    }

    private async Task HandleUpdateEventAsync()
    {
        var now = DateTime.UtcNow;
        if (now <= _lastUpdateDate.AddTicks(_minDelayBetweenUpdates.Ticks)) return;

        await _semaphore.WaitAsync();
        try
        {
            if (now <= _lastUpdateDate.AddTicks(_minDelayBetweenUpdates.Ticks)) return;
            _lastUpdateDate = DateTime.UtcNow;

            var desiredState = BuildGridDesiredState();
            await EnsureGridStateAsync(desiredState);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private GridState BuildGridDesiredState() =>
        new(
            GetDesiredBuyLines().ToArray(),
            GetDesiredSellLines().ToArray()
        );

    private async Task EnsureGridStateAsync(GridState desiredState)
    {
        var currentState = GetCurrentGridState();
        var cancelRequests = GetCancelRequestsFromStates(desiredState, currentState)
            .ToArray();
        var placeRequests = GetOrderPlaceRequestsFromStates(desiredState, currentState)
            .Where(r => !_pendingOrderIds.ContainsKey(r.GetGridLineIndex()))
            .ToArray();

        if (_log.IsEnabled(LogLevel.Debug))
        {
            _log.LogDebug("{Symbol} - DESIRED: {DesiredState}", _tradedSymbol.Symbol, desiredState);
            _log.LogDebug("{Symbol} - CURRENT: {CurrentState}", _tradedSymbol.Symbol, currentState);
            _log.LogDebug("{Symbol} -  CANCEL: {CancelOrders}", _tradedSymbol.Symbol, cancelRequests.Select(r => r.GetGridLineIndex()));
            _log.LogDebug("{Symbol} -   PLACE: {PlaceOrders}", _tradedSymbol.Symbol, placeRequests.Select(r => r.GetGridLineIndex()));
        }

        SetupPendingOrders(placeRequests.Select(l => l.GetGridLineIndex()));

        if (cancelRequests.Length > 0) await _okx.CancelOrdersAsync(cancelRequests);
        if (placeRequests.Length > 0) await _okx.PlaceOrdersAsync(placeRequests);
    }

    private void SetupPendingOrders(IEnumerable<int> pendingLines)
    {
        foreach (var pendingLine in pendingLines)
        {
            SetupPendingOrder(pendingLine);
        }
    }

    private void SetupPendingOrder(int pendingLine)
    {
        _pendingOrderIds.AddOrUpdate(pendingLine, byte.MinValue, (k, b) => b);
        StartPendingOrderCleanupTask(pendingLine);
    }

    private void StartPendingOrderCleanupTask(int pendingLine)
    {
        var cts = new CancellationTokenSource();
        _pendingOrderRemovalTaskTokens.AddOrUpdate(pendingLine, cts, (l, _) => cts);

        _ = Task.Run(async () =>
        {
            await Task.Delay(_pendingOrderTimeout, cts.Token);
            if (cts.IsCancellationRequested) return;

            _log.LogWarning("{Symbol} - Did not receive a result for pending order {OrderId} within {PendingOrderTimeout}", _tradedSymbol.Symbol, pendingLine, _pendingOrderTimeout);

            _pendingOrderIds.Remove(pendingLine, out _);
        }, cts.Token);
    }

    private void CancelPendingOrderCleanupTask(int line)
    {
        if (_pendingOrderRemovalTaskTokens.Remove(line, out var cts))
        {
            cts.Cancel();
        }
    }

    private IEnumerable<OKXOrderCancelRequest> GetCancelRequestsFromStates(GridState desiredState, GridState currentState)
    {
        /*
         *  currentState not in desiredState => cancel
         */
        var buyCancelLines = CreateCancelRequestsFromGridLineInfos(
            currentState.BuyOrderLines.ExceptBy(desiredState.BuyOrderLines.Select(l => l.Line), l => l.Line)
        );
        var sellCancelLines = CreateCancelRequestsFromGridLineInfos(
            currentState.SellOrderLines.ExceptBy(desiredState.SellOrderLines.Select(l => l.Line), l => l.Line)
        );

        var duplicateLines = CreateCancelRequestsFromGridLineInfos(
            currentState.BuyOrderLines.Concat(currentState.SellOrderLines)
                .GroupBy(l => l.Line)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Skip(1))
        );

        var cancelRequests = buyCancelLines.Concat(sellCancelLines).Concat(duplicateLines);

        return cancelRequests;
    }

    private IEnumerable<OKXOrderPlaceRequest> GetOrderPlaceRequestsFromStates(GridState desiredState, GridState currentState)
    {
        /*
         * desiredState not in currentState => place
         */
        var buyPlaceLines = CreatePlaceRequestsFromGridLineInfos(
            desiredState.BuyOrderLines.ExceptBy(currentState.BuyOrderLines.Select(l => l.Line), l => l.Line),
            OKXOrderSide.Buy
        );
        var sellPlaceLines = CreatePlaceRequestsFromGridLineInfos(
            desiredState.SellOrderLines.ExceptBy(currentState.SellOrderLines.Select(l => l.Line), l => l.Line),
            OKXOrderSide.Sell
        );

        var placeRequests = buyPlaceLines.Concat(sellPlaceLines);

        return placeRequests;
    }

    private IEnumerable<OKXOrderCancelRequest> CreateCancelRequestsFromGridLineInfos(
        IEnumerable<GridLineInfo> gridLineInfos) =>
        gridLineInfos
            .Where(l => l.Order != null)
            .Select(l => new OKXOrderCancelRequest
            {
                Symbol = _symbol.Symbol,
                OrderId = l.Order!.OrderId.ToString(),
                ClientOrderId = l.Order!.ClientOrderId
            });

    private IEnumerable<OKXOrderPlaceRequest> CreatePlaceRequestsFromGridLineInfos(
        IEnumerable<GridLineInfo> gridLineInfos, OKXOrderSide orderSide) =>
        gridLineInfos
            .Select(l => new OKXOrderPlaceRequest
            {
                Symbol = _symbol.Symbol,
                ClientOrderId = GridLineOrderId.Create(l.Line, orderSide, 0),
                Price = l.Price,
                Quantity = GetPositionQuantity(l.Price),
                OrderSide = orderSide,
                PositionSide = _positionSide,
                OrderType = _orderType,
                TradeMode = _tradeMode,
                ReduceOnly = orderSide == OKXOrderSide.Sell
            });

    private GridState GetCurrentGridState()
    {
        var currentOrders = _okx.GetCurrentOrders(_symbol).ToArray();
        var buyOrders = currentOrders
            .GetActiveOrdersOfSide(OKXOrderSide.Buy)
            .ToGridLineInfos()
            .ToArray();
        var sellOrders = currentOrders
            .GetActiveOrdersOfSide(OKXOrderSide.Sell)
            .ToGridLineInfos()
            .ToArray();

        return new GridState(buyOrders, sellOrders);
    }

    private IEnumerable<GridLineInfo> GetDesiredBuyLines()
    {
        return GridCalculator.GetGridBuyLinesAndPrices(
            _currentGridLine,
            _tradedSymbol.TakeProfitPercent,
            _tradedSymbol.MaxActiveBuyOrders);
    }

    private IEnumerable<GridLineInfo> GetDesiredSellLines()
    {
        if (_position == null) yield break;
        var quantity = _position.PositionsQuantity.GetValueOrDefault();
        if (quantity == 0) yield break;

        _log.LogTrace("{Symbol} - Building SELL desired state. Found position with quantity: {PositionQuantity}", _tradedSymbol.Symbol, quantity);

        foreach (var gridLine in GridCalculator.GetGridSellLinesAndPrices(_currentGridLine, _tradedSymbol.TakeProfitPercent, _tradedSymbol.MaxActiveSellOrders))
        {
            if (quantity <= 0) break;

            var gridLineQuantity = GetPositionQuantity(gridLine.Price);
            quantity -= gridLineQuantity;

            _log.LogTrace("{Symbol} - Adding SELL desired line {OrderId}. Remaining quantity: {PositionQuantity}", _tradedSymbol.Symbol, gridLine.Line, quantity);

            yield return gridLine;
        }
    }

    private decimal GetPositionQuantity(decimal price)
    {
        if (_symbol == null)
        {
            throw new InvalidOperationException("Instrument not found.");
        }

        if (!_symbol.ContractValue.HasValue)
        {
            throw new InvalidOperationException("Cannot calculate quantity for instrument without contract value.");
        }

        var contractValue = _symbol.ContractValue.Value * price;
        var leveragedAmount = _tradedSymbol.InvestPerGrid * _tradedSymbol.Leverage;
        var quantity = Math.Round(leveragedAmount / contractValue, _symbol.LotSize.Scale, MidpointRounding.ToZero);
        if (quantity < _symbol.LotSize) quantity = _symbol.LotSize;
        if (quantity % _symbol.LotSize != 0)
        {
            quantity -= quantity % _symbol.LotSize;
        }

        return quantity;
    }
}
