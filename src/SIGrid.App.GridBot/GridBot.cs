using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Requests;
using JasperFx.Core;
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

// AbstractGridBot - FuturesGridBot, SpotGridBot - 

public class GridBot
{
    private readonly OKXPositionSide _positionSide = OKXPositionSide.Long;
    private readonly OKXTradeMode _tradeMode = OKXTradeMode.Cross;
    private readonly OKXMarginMode _marginMode = OKXMarginMode.Cross;
    private readonly OKXOrderType _orderType = OKXOrderType.LimitOrder;
    private readonly TimeSpan _minDelayBetweenUpdates = TimeSpan.FromSeconds(0.5);
    private readonly TimeSpan _pendingOrderTimeout = TimeSpan.FromSeconds(5);
    
    private readonly SIGridOptions.TradedSymbolOptions _tradedSymbol;
    private readonly OKXConnector _okx;
    private readonly ILogger<GridBot> _log;
    
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<int, byte> _pendingOrderIds = new();
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
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("{Symbol} - Starting Up Bot: {BotConfig}", _tradedSymbol.Symbol, JsonConvert.SerializeObject(_tradedSymbol, Formatting.None));
        
        await LoadRequiredInformationAsync(stoppingToken);
        _ = Task.Delay(_minDelayBetweenUpdates, stoppingToken)
            .ContinueWith(async _ => await CancelAllPendingOrders(), stoppingToken);
        await SubscribeToExchangeUpdates(stoppingToken);
        await CancelAllPendingOrders();
    }

    public async Task CancelAllPendingOrders()
    {
        var cancelRequests = _okx.GetCurrentOrders(_symbol)
            .Where(o => !string.IsNullOrWhiteSpace(o.ClientOrderId) && o.GetGridLineIndex() > 0)
            .Select(o => new OKXOrderCancelRequest
            {
                OrderId = o.OrderId.ToString(),
                ClientOrderId = o.ClientOrderId,
                Symbol = o.Symbol
            });

        await _okx.CancelOrdersAsync(cancelRequests);
    }

    private void UpdateCurrentPrice(decimal? newPrice)
    {
        _currentPrice = newPrice ?? _currentPrice;
        
        // Spot has always sell grid lines active if there is enough balance. No need to update the grid on price action.
        if (_symbol.InstrumentType == OKXInstrumentType.Spot) return;

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

        var positions = await _okx.GetOpenPositionsAsync(_symbol);
        _position = positions.FirstOrDefault(p => p.PositionSide == _positionSide);
    }

    private async Task SubscribeToExchangeUpdates(CancellationToken ct)
    {
        var symbolTicker = _okx.GetSymbolTickerSubscription(_symbol, ct)
            .Sample(_minDelayBetweenUpdates)
            .ToAsyncEnumerable();

        var positionUpdates = _symbol.InstrumentType == OKXInstrumentType.Spot
            ? AsyncEnumerable.Empty<OKXPosition>()
            : _okx.GetPositionUpdateSubscription(_symbol)
                .Sample(_symbol.InstrumentType == OKXInstrumentType.Spot ? _minDelayBetweenUpdates * 2 : _minDelayBetweenUpdates)
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
                if (positionUpdate.MarginMode != _marginMode) continue;
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

        await UpdateGridStateAsync();
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

        await UpdateGridStateAsync();
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

        await UpdateGridStateAsync();
    }

    private async Task<decimal?> GetPriceForImmediateOpenPositionOrderAsync()
    {
        var ticker = await _okx.GetTickerAsync(_symbol);
        if (ticker == null || !ticker.BestBidPrice.HasValue || !ticker.BestAskPrice.HasValue)
        {
            _log.LogWarning("{Symbol} - Ticker not available. Opening at market price.", _symbol.Symbol);
            return null;
        }

        var spread = ticker.BestAskPrice.Value - ticker.BestBidPrice.Value;
        if (spread > _symbol.TickSize)
        {
            return ticker.BestAskSize!.Value - _symbol.TickSize;
        }

        return ticker.BestBidPrice.Value;
    }

    private async Task UpdateGridStateAsync()
    {
        var now = DateTime.UtcNow;
        if (now <= _lastUpdateDate.AddTicks(_minDelayBetweenUpdates.Ticks)) return;

        await _semaphore.WaitAsync();
        try
        {
            if (now <= _lastUpdateDate.AddTicks(_minDelayBetweenUpdates.Ticks)) return;
            _lastUpdateDate = DateTime.UtcNow;
            
            var desiredState = await BuildGridDesiredState();
            await EnsureGridStateAsync(desiredState);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<GridState> BuildGridDesiredState() =>
        new(
            await GetDesiredBuyLines(),
            GetDesiredSellLines()
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
            currentState.BuyOrderLines.ExceptBy(desiredState.BuyOrderLines, l => l, GridLineInfoEqualityComparer.Instance)
        );
        var sellCancelLines = CreateCancelRequestsFromGridLineInfos(
            currentState.SellOrderLines.ExceptBy(desiredState.SellOrderLines, l => l, GridLineInfoEqualityComparer.Instance)
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
            desiredState.BuyOrderLines.ExceptBy(currentState.BuyOrderLines, l => l, GridLineInfoEqualityComparer.Instance),
            OKXOrderSide.Buy
        );
        var sellPlaceLines = CreatePlaceRequestsFromGridLineInfos(
            desiredState.SellOrderLines.ExceptBy(currentState.SellOrderLines, l => l, GridLineInfoEqualityComparer.Instance),
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
            .Select(l => GetOrderPlaceRequestForGridLineInfo(l, orderSide));

    private OKXOrderPlaceRequest GetOrderPlaceRequestForGridLineInfo(GridLineInfo gridLine, OKXOrderSide orderSide)
    {
        var request = new OKXOrderPlaceRequest
        {
            Symbol = _symbol.Symbol,
            ClientOrderId = GridLineOrderId.Create(gridLine.Line, orderSide, 0),
            Price = gridLine.Price,
            Quantity = GetPositionQuantity(gridLine.Price, orderSide),
            OrderSide = orderSide,
            OrderType = _orderType,
        };

        switch (_symbol.InstrumentType)
        {
            case OKXInstrumentType.Any:
                break;
            case OKXInstrumentType.Spot:
                request.Asset = _symbol.BaseAsset;
                request.TradeMode = OKXTradeMode.Cash;
                break;
            case OKXInstrumentType.Margin:
                request.TradeMode = _tradeMode;
                break;
            case OKXInstrumentType.Swap:
                request.TradeMode = _tradeMode;
                request.PositionSide = _positionSide;
                request.ReduceOnly = orderSide == OKXOrderSide.Sell;
                request.QuantityType = OKXQuantityAsset.QuoteAsset;
                break;
            case OKXInstrumentType.Futures:
                break;
            case OKXInstrumentType.Option:
                break;
            case OKXInstrumentType.Contracts:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return request;
    }

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

    private async Task<GridLineInfo[]> GetDesiredBuyLines()
    {
        var gridLineInfos = GridCalculator.GetGridBuyLinesAndPrices(
            _currentGridLine,
            _tradedSymbol.TakeProfitPercent,
            _tradedSymbol.MaxActiveBuyOrders,
            (price) => GetPositionQuantity(price, OKXOrderSide.Buy),
            _symbol.TickSize.Scale);
        
        if (_symbol.InstrumentType != OKXInstrumentType.Spot && !HasOpenPosition())
        {
            gridLineInfos = gridLineInfos.Prepend(await GetDesiredBuyLineForImmediatePositionOpenAsync());
        }

        return gridLineInfos.Where(IsGridLineWithinMinAndMaxPriceRange).ToArray();
    }

    private async Task<GridLineInfo> GetDesiredBuyLineForImmediatePositionOpenAsync()
    {
        var price = await GetPriceForImmediateOpenPositionOrderAsync() ?? _currentPrice;
        return new GridLineInfo(_currentGridLine, price, GetPositionQuantity(price, OKXOrderSide.Buy));
    }

    private GridLineInfo[] GetDesiredSellLines()
    {
        return (_tradedSymbol.SymbolType.Equals(nameof(OKXInstrumentType.Spot),
                    StringComparison.InvariantCultureIgnoreCase)
                    ? GetDesiredSellLinesForSpot()
                    : GetDesiredSellLinesForPosition()
            )
            .Where(IsGridLineWithinMinAndMaxPriceRange)
            .ToArray();
    }

    private bool IsGridLineWithinMinAndMaxPriceRange(GridLineInfo gridLineInfo)
    {
        return gridLineInfo.Price >= (_tradedSymbol.MinPrice ?? 0) && gridLineInfo.Price <= (_tradedSymbol.MaxPrice ?? decimal.MaxValue);
    }

    private IEnumerable<GridLineInfo> GetDesiredSellLinesForSpot()
    {
        return GridCalculator.GetGridSellLinesAndPrices(
            _currentGridLine,
            _tradedSymbol.TakeProfitPercent,
            _tradedSymbol.MaxActiveSellOrders,
            (price) => GetPositionQuantity(price, OKXOrderSide.Sell),
            _symbol.TickSize.Scale);
    }

    private IEnumerable<GridLineInfo> GetDesiredSellLinesForPosition()
    {
        if (!HasOpenPosition()) yield break;
        var availableQuantity = _position!.PositionsQuantity.GetValueOrDefault();
        if (availableQuantity == 0) yield break;

        _log.LogTrace("{Symbol} - Building SELL desired state. Found position with quantity: {PositionQuantity}", _tradedSymbol.Symbol, availableQuantity);

        foreach (var gridLine in GridCalculator.GetGridSellLinesAndPrices(
                     _currentGridLine,
                     _tradedSymbol.TakeProfitPercent,
                     _tradedSymbol.MaxActiveSellOrders,
                     (price) => GetPositionQuantity(price, OKXOrderSide.Sell),
                     _symbol.TickSize.Scale)
                 )
        {
            var gridLineQuantity = GetPositionQuantity(gridLine.Price, OKXOrderSide.Sell);

            if (availableQuantity - gridLineQuantity < 0)
            {
                gridLineQuantity = availableQuantity;
            }

            availableQuantity -= gridLineQuantity;

            if (gridLineQuantity <= 0)
            {
                break;
            }

            _log.LogTrace("{Symbol} - Adding SELL desired line {OrderId}. Remaining quantity: {PositionQuantity}", _tradedSymbol.Symbol, gridLine.Line, availableQuantity);

            yield return gridLine;
        }
    }

    private decimal GetPositionQuantity(decimal price, OKXOrderSide orderSide)
    {
        if (_symbol == null)
        {
            throw new InvalidOperationException("Instrument not found.");
        }

        return _symbol.InstrumentType switch
        {
            OKXInstrumentType.Swap or OKXInstrumentType.Futures when _symbol.ContractValue.HasValue =>
                GetLeveragedPositionQuantity(price),
            OKXInstrumentType.Spot => GetSpotPositionQuantity(price, orderSide),
            _ => throw new InvalidOperationException(
                $"{_symbol.Symbol} - No implementation for position quantity calculation of instrument type '{_symbol.InstrumentType}'.")
        };
    }

    private decimal GetSpotPositionQuantity(decimal price, OKXOrderSide orderSide)
    {
        return _tradedSymbol.InvestCurrency switch
        {
            SIGridOptions.TradedSymbolOptions.InvestCurrencyType.Base => GetSpotPositionQuantityForBaseAsset(),
            SIGridOptions.TradedSymbolOptions.InvestCurrencyType.Quote => GetSpotPositionQuantityForQuoteAsset(price, orderSide),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private decimal GetSpotPositionQuantityForBaseAsset()
    {
        return _tradedSymbol.InvestPerGrid < _symbol.MinimumOrderSize
            ? _symbol.MinimumOrderSize
            : _tradedSymbol.InvestPerGrid;
    }

    private decimal GetSpotPositionQuantityForQuoteAsset(decimal price, OKXOrderSide orderSide)
    {
        var quantity = _tradedSymbol.InvestPerGrid / price;

        if (orderSide == OKXOrderSide.Sell)
        {
            var fees = _feeRate.Maker.GetValueOrDefault() * (quantity * price) * 2;
            var feesQuantity = fees / price;
            quantity += Math.Abs(feesQuantity);
        }

        quantity = Math.Round(quantity, _symbol.LotSize.Scale, MidpointRounding.ToZero);

        if (quantity < _symbol.MinimumOrderSize) return _symbol.MinimumOrderSize;
        if (quantity % _symbol.LotSize != 0) quantity -= quantity % _symbol.LotSize;
        return quantity;
    }

    private decimal GetLeveragedPositionQuantity(decimal price)
    {
        if (!_symbol.ContractValue.HasValue)
        {
            throw new InvalidOperationException("Cannot calculate leveraged position size without contract value.");
        }

        decimal usdValuePerContract;

        if (_symbol is { InstrumentType: OKXInstrumentType.Swap, ContractType: OKXContractType.Inverse })
        {
            usdValuePerContract = _symbol.ContractValue.Value;
        }
        else
        {
            usdValuePerContract = _symbol.ContractValue.Value * price;
        }

        var leveragedAmount = _tradedSymbol.InvestPerGrid * _tradedSymbol.Leverage;
        var quantity = Math.Round(leveragedAmount / usdValuePerContract, _symbol.LotSize.Scale, MidpointRounding.ToZero);
        if (quantity < _symbol.LotSize) quantity = _symbol.LotSize;
        if (quantity % _symbol.LotSize != 0)
        {
            quantity -= quantity % _symbol.LotSize;
        }

        return quantity;
    }

    private bool HasOpenPosition()
    {
        return _position?.PositionsQuantity > 0;
    }
}
