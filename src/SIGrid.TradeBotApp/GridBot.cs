using System.Diagnostics;
using System.Reactive.Linq;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using OKX.Net.Objects.Market;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.Exchange.Shared;
using SIGrid.Exchange.Shared.Extensions;
using SIGrid.Telemetry;
using SIGrid.TradeBot;
using SIGrid.TradeBot.Extensions;

namespace SIGrid.TradeBotApp;

public class GridBot : BackgroundService
{
    private readonly SIGridOptions.TradedSymbolOptions _tradedSymbol;
    private readonly OKXExchangeConnector _okx;
    private readonly ILogger<GridBot> _log;
    private readonly TelemetrySender _telemetrySender;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly OKXInstrumentType _instrumentType;
    private readonly TimeSpan _maxWaitTimeBetweenOrderCreations = TimeSpan.FromSeconds(0.5);
    private readonly long _ticksUpdateGridDelay;
    private readonly SemaphoreSlim _orderLock = new(1, 1);
    private readonly Dictionary<int, OKXOrderState> _gridLineOrderStates = new();
    private readonly TimeSpan _lastUpdateCheckInterval = TimeSpan.FromMinutes(30);
    
    private OKXInstrument? _instrument = null;
    private OKXFeeRate? _feeRate = null;
    private Ref<decimal> _currentPrice = -1;
    private OKXPosition? _position = null;
    private Ref<decimal> _activeQuantity = 0;
    private OKXOrder? _lastSellOrderFilled = null;
    private OKXOrder? _lastBuyOrderFilled = null;
    private OKXOrderPlaceResponse[]? _lastPlaceResponses = null;
    private Timer? _orderFixTimer;
    private int _timesNoActiveOrders = 0;
    private Ref<DateTime>? _lastUpdateDate;
    private Timer? _checkLastUpdateTimer;
    private long _lastGridUpdateTime = -1;

    private const int OrderFixTimerSeconds = 5;
    private const int MaxTimesNoActiveOrders = 30 / OrderFixTimerSeconds;

    public GridBot(SIGridOptions.TradedSymbolOptions tradedSymbol, OKXExchangeConnector okx, ILogger<GridBot> log, TelemetrySender telemetrySender)
    {
        _tradedSymbol = tradedSymbol;
        _okx = okx;
        _log = log;
        _telemetrySender = telemetrySender;
        _instrumentType = tradedSymbol.SymbolType.ToOKXInstrumentTypeOrException();

        _ticksUpdateGridDelay = _maxWaitTimeBetweenOrderCreations.Ticks;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitSymbolInfoAsync();
        await InitActiveOrdersAsync();

        _subscriptions.Add(_okx.GetSymbolTickerSubscription(_tradedSymbol.Symbol, stoppingToken)
            .Sample(TimeSpan.FromMilliseconds(500))
            .Select(t => Observable.FromAsync(async () => await HandleTickAsync(t)))
            .Concat()
            .Subscribe()
        );

        _subscriptions.Add(_okx.GetPositionSubscription(_tradedSymbol.SymbolType, _tradedSymbol.Symbol, stoppingToken)
            .Where(p => p.MarginMode == OKXMarginMode.Cross)
            .Select(p => Observable.FromAsync(async () => await HandlePositionUpdateAsync(p)))
            .Concat()
            .Subscribe()
        );

        _subscriptions.Add(_okx.GetOrderSubscription(_tradedSymbol.SymbolType, _tradedSymbol.Symbol, stoppingToken)
            .Select(o => Observable.FromAsync(async () => await HandleOrderUpdateAsync(o)))
            .Concat()
            .Subscribe()
        );

        _subscriptions.Add(_okx.GetSymbolInfoSubscription()
            .Where(s => s.InstrumentType == _instrumentType && s.Symbol == _tradedSymbol.Symbol)
            .Do(instrument => _instrument = instrument)
            .Subscribe()
        );

        _orderFixTimer = new Timer(FixOrdersAsync, null, TimeSpan.FromSeconds(OrderFixTimerSeconds), TimeSpan.FromSeconds(OrderFixTimerSeconds));
        _checkLastUpdateTimer = new Timer(ValidateLastUpdateDateOrShutdown, null, _lastUpdateCheckInterval, _lastUpdateCheckInterval);

        await Task.Delay(-1, stoppingToken);
    }

    private void ValidateLastUpdateDateOrShutdown(object? _)
    {
        if (_lastUpdateDate == null || _lastUpdateDate >= DateTime.UtcNow.Add(_lastUpdateCheckInterval.Multiply(-1))) return;

        _log.LogError("{Symbol} - Did not receive any update within 30 minutes. Exiting", _tradedSymbol.Symbol);
        Program.ApplicationStoppingToken.Cancel();
    }

    private void UpdateLastUpdateDate()
    {
        _lastUpdateDate = DateTime.UtcNow;
    }

    private async void FixOrdersAsync(object? _)
    {
        var activeOrders = _okx.GetActiveOrders(_tradedSymbol.SymbolType, _tradedSymbol.Symbol)
            .Where(o => o.OrderState is OKXOrderState.Live or OKXOrderState.PartiallyFilled)
            .ToArray();

        await Task.WhenAll(activeOrders.GroupBy(o => o.GetGridLineIndex())
            .Where(g => g.Count() > 1)
            .Select(async g =>
            {
                var cancelRequests = g.OrderByDescending(o => o.OrderState)
                    .Skip(1)
                    .Select(o => new OKXOrderCancelRequest
                    {
                        OrderId = o.OrderId.ToString()
                    })
                    .ToArray();

                _log.LogWarning("{Symbol} - Cancelling duplicate orders: {OrderIds}", _tradedSymbol.Symbol, string.Join(", ", cancelRequests.Select(r => r.GetGridLineIndex())));
                await _okx.CancelOrdersAsync(cancelRequests);
            }));

        CheckActiveOrders(activeOrders.Length);
    }

    private void CheckActiveOrders(int activeOrdersCount)
    {
        if (activeOrdersCount > 0)
        {
            _timesNoActiveOrders = 0;
            return;
        }

        _log.LogWarning("{Symbol} - No active orders found!", _tradedSymbol.Symbol);
        ++_timesNoActiveOrders;

        if (_timesNoActiveOrders >= MaxTimesNoActiveOrders && !Debugger.IsAttached)
        {
            _log.LogCritical("{Symbol} - No active orders {TimesNoActiveOrders} times. Stopping program.",
                _tradedSymbol.Symbol, _timesNoActiveOrders);
            Program.ApplicationStoppingToken.Cancel();
        }
    }

    private async Task InitSymbolInfoAsync()
    {
        _instrument = await _okx.GetSymbolInfo(_tradedSymbol.SymbolType, _tradedSymbol.Symbol);
        if (_instrument == null)
        {
            throw new ArgumentNullException(nameof(_tradedSymbol.Symbol), "Invalid symbol. Symbol not found on exchange.");
        }

        _feeRate = await _okx.GetFeeRateAsync(_instrument);
        if (_feeRate == null)
        {
            throw new ArgumentNullException(nameof(_tradedSymbol.Symbol), "Invalid symbol. No fee rates found.");
        }

        _log.LogInformation("{Symbol} - Loaded symbol info for {Symbol}", _instrument.Symbol, _instrument.Symbol);
    }

    private async Task InitActiveOrdersAsync()
    {
        var activeOrders = (await _okx.GetActiveOrdersAsync(_tradedSymbol.SymbolType, _tradedSymbol.Symbol)).ToArray();

        var buyOrders = activeOrders.Where(o => o.OrderSide == OKXOrderSide.Buy).ToArray();
        var cancelRequests = buyOrders.Select(o => new OKXOrderCancelRequest
        {
            Symbol = _tradedSymbol.Symbol,
            OrderId = o.OrderId.ToString(),
        }).ToArray();
        if (cancelRequests.Length > 0)
        {
            _log.LogInformation("{Symbol} - Cancelling {NumOrders} active buy orders.", _tradedSymbol.Symbol, cancelRequests.Length);
            var result = await _okx.CancelOrdersAsync(cancelRequests);
            var failedOrders = result.Where(o => o.Code != "0").ToArray();
            if (failedOrders.Length > 0)
            {
                _log.LogWarning("{Symbol} - Error cancelling orders: {messages}", _tradedSymbol.Symbol, string.Join(", ", failedOrders.Select(f => f.Message)));
                cancelRequests = cancelRequests.Where(r => failedOrders.Any(o => r.OrderId == o.OrderId.ToString())).ToArray();
                result = await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ContinueWith(async _ => await _okx.CancelOrdersAsync(cancelRequests))
                    .Unwrap();
                if (result.Any(r => r.Code != "0"))
                {
                    _log.LogWarning("{Symbol} - Unable to cancel active buy orders!", _tradedSymbol.Symbol);
                }
            }
        }

        foreach (var activeOrder in activeOrders.Except(buyOrders))
        {
            if (!GridLineOrderId.TryParse(activeOrder.ClientOrderId, out var gridOrderId))
            {
                _log.LogWarning("{Symbol} - Could not parse grid order id for order {OrderId}", _tradedSymbol.Symbol, activeOrder.OrderId);
                continue;
            }

            _gridLineOrderStates[gridOrderId.LineIndex] = activeOrder.OrderState;
        }

        _log.LogInformation("{Symbol} - Found {NumActiveOrders} active orders.", _tradedSymbol.Symbol, activeOrders.Length - buyOrders.Length);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _okx.CancelOrdersAsync(_okx.GetActiveOrders(_tradedSymbol.SymbolType, _tradedSymbol.Symbol)
            .Where(o => o.OrderId.HasValue && GridLineOrderId.TryParse(o.ClientOrderId, out _))
            .Select(o => new OKXOrderCancelRequest
            {
                Symbol = o.Symbol,
                OrderId = o.OrderId!.Value.ToString(),
            }));

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleOrderUpdateAsync(OKXOrderUpdate orderUpdate)
    {
        UpdateLastUpdateDate();

        if (!GridLineOrderId.TryParse(orderUpdate.ClientOrderId, out var gridOrderId))
        {
            return;
        }

        _gridLineOrderStates[gridOrderId.LineIndex] = orderUpdate.OrderState;

        _telemetrySender.WriteOrderUpdateAsync(orderUpdate.ToTelemetryOrderUpdate(_tradedSymbol));

        var action = orderUpdate.OrderState == OKXOrderState.Filled
            ? orderUpdate.OrderSide == OKXOrderSide.Buy ? "PLACE_SELL" : "CALCULATE_PROFIT"
            : orderUpdate.OrderState == OKXOrderState.Canceled
                ? "UPDATE_ORDERS (Cancelled)"
                : orderUpdate.OrderState == OKXOrderState.Live
                    ? "UPDATE_ORDERS (Live)"
                    : orderUpdate.OrderState == OKXOrderState.PartiallyFilled
                        ? "UPDATE_ORDERS (PartiallyFilled)"
                        : "UNKNOWN";
        _log.LogInformation("{Symbol} - ORDER: {OrderId} - SIDE: {OrderSide} - STATE: {OrderState} - FILLED: {OrderQuantityFilled}/{OrderQuantity} - ACTION: {OrderAction}", orderUpdate.Symbol, gridOrderId.LineIndex, orderUpdate.OrderSide, orderUpdate.OrderState, orderUpdate.QuantityFilled, orderUpdate.Quantity, action);

        switch (orderUpdate.OrderSide)
        {
            case OKXOrderSide.Buy:
                if (orderUpdate.OrderState == OKXOrderState.Filled)
                {
                    Interlocked.Exchange(ref _lastBuyOrderFilled, orderUpdate);
                    Interlocked.Exchange(ref _lastSellOrderFilled, null); // Reset last filled sell order.
                }
                break;
            case OKXOrderSide.Sell:
                if (orderUpdate.OrderState == OKXOrderState.Filled)
                {
                    Interlocked.Exchange(ref _lastSellOrderFilled, orderUpdate);
                    UpdateActivePositionQuantity(orderUpdate);
                    await CalculateAndDisplayProfit(orderUpdate);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        await UpdateGridAsync(orderUpdate);
    }

    private void UpdateActivePositionQuantity(OKXOrderUpdate orderUpdate)
    {
        if (orderUpdate.OrderState == OKXOrderState.Filled)
        {
            switch (orderUpdate.OrderSide)
            {
                case OKXOrderSide.Buy:
                    _activeQuantity += orderUpdate.Quantity;
                    break;
                case OKXOrderSide.Sell:
                    _activeQuantity -= orderUpdate.Quantity;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private async Task CalculateAndDisplayProfit(OKXOrderUpdate triggeringOrder)
    {
        if (_instrument == null)
        {
            _log.LogWarning("{Symbol} - Received order update without loaded instrument info. Discarding.", _tradedSymbol.Symbol);
            return;
        }

        if (!GridLineOrderId.TryParse(triggeringOrder.ClientOrderId, out var gridOrderId))
        {
            _log.LogWarning("{Symbol} - Got an order without grid order id. Manually placed? OrderId: {OrderId}", _tradedSymbol.Symbol, triggeringOrder.OrderId);
            return;
        }

        var buyGridLine = gridOrderId.LineIndex - 1;
        var buyOrder = await _okx.GetOrderUpdateByGridLine(_tradedSymbol.SymbolType, _tradedSymbol.Symbol, buyGridLine);

        decimal? buyVolume, buyFee;

        if (buyOrder != null)
        {
            if (buyOrder is OKXOrderUpdate orderUpdate)
            {
                buyVolume = orderUpdate.FillNotionalUsd;
                buyFee = orderUpdate.GetFillFeeNotionalUsd(_instrument);
            }
            else
            {
                (buyVolume, buyFee) = CalculateVolumeAndFee(buyOrder.Quantity.GetValueOrDefault(), buyOrder.FillPrice.GetValueOrDefault(), false);
            }
        }
        else
        {
            var buyPrice = GridCalculator.GetGridPrice(_tradedSymbol.TakeProfitPercent, gridOrderId.LineIndex - 1);
            (buyVolume, buyFee) = CalculateVolumeAndFee(triggeringOrder.Quantity.GetValueOrDefault(), buyPrice, false);
        }

        var profit = triggeringOrder.FillNotionalUsd - buyVolume - Math.Abs(buyFee ?? 0) - Math.Abs(triggeringOrder.GetFillFeeNotionalUsd(_instrument));
        var profitBase = profit / _currentPrice;

        string baseAsset = string.IsNullOrWhiteSpace(_instrument.BaseAsset) 
            ? (_instrument?.Underlying ?? _instrument?.Symbol ?? "UNKNOWN-").Split("-").FirstOrDefault() ?? "UNKNOWN"
            : _instrument.BaseAsset;

        _log.LogInformation("{Symbol} - Grid Line Profit: {Profit:0.####} ({BaseAsset}: {ProfitBase:0.########}) - Grid Line: {GridLine} - Exchange PnL {ExchangePnL:0.####}", _tradedSymbol.Symbol, profit, baseAsset, profitBase, gridOrderId.LineIndex, triggeringOrder.FillPnl);

        (decimal Volume, decimal Fee) CalculateVolumeAndFee(decimal quantity, decimal price, bool isTaker)
        {
            decimal volume;
            switch (_instrument.ContractType)
            {
                case OKXContractType.Linear:
                    volume = quantity * price;
                    return (volume, GetFeeForVolume(volume, isTaker));
                case OKXContractType.Inverse:
                    volume = _instrument.ContractValue * quantity ?? 0;
                    return (volume, GetFeeForVolume(volume, isTaker));
                default:
                    return (0, 0);
            }
        }
    }

    private decimal GetFeeForVolume(decimal? volume, bool isTaker)
    {
        if (_feeRate == null)
        {
            throw new ArgumentNullException(nameof(_feeRate), "Fee rate not set.");
        }

        if (!volume.HasValue)
        {
            return 0;
        }

        var rate = isTaker
            ? new []{_feeRate.Taker, _feeRate.TakerFeeUsdc, _feeRate.TakerUsdtMarginContracts}.Max() 
            : new []{_feeRate.Maker, _feeRate.MakerFeeUsdc, _feeRate.MakerUsdtMarginContracts}.Max();
        var fee = volume * rate;
        return fee ?? 0;
    }

    private Task HandlePositionUpdateAsync(OKXPosition position)
    {
        UpdateLastUpdateDate();

        if (position is { PositionSide: OKXPositionSide.Long, MarginMode: OKXMarginMode.Cross })
        {
            Interlocked.Exchange(ref _position, position);
            Interlocked.Exchange(ref _activeQuantity, position.PositionsQuantity);
            //_log.LogInformation("{Symbol} - {Side} - SIZE: {Size} - VALUE: {Value}", position.Symbol, position.PositionSide, position.PositionsQuantity, position.PositionsQuantity * _currentPrice);
        }

        return Task.CompletedTask;
    }

    private async Task HandleTickAsync(OKXTicker tick)
    {
        UpdateLastUpdateDate();

        if (!tick.LastPrice.HasValue)
        {
            return;
        }

        _telemetrySender.WritePriceUpdateAsync(new TelemetrySymbolPriceUpdate(_tradedSymbol, _currentPrice));

        Interlocked.Exchange(ref _currentPrice, tick.LastPrice.Value);
        await UpdateGridAsync();
    }

    private async Task UpdateGridAsync(OKXOrderUpdate? triggeringOrder = null)
    {
        if (_currentPrice < 0 ) return;

        if (_lastGridUpdateTime + _ticksUpdateGridDelay > DateTime.UtcNow.Ticks) return;
        
        if (!await _orderLock.WaitAsync(TimeSpan.FromMilliseconds(500)))
        {
            return;
        }

        if (_lastGridUpdateTime + _ticksUpdateGridDelay > DateTime.UtcNow.Ticks) return;

        try
        {
            _lastGridUpdateTime = DateTime.UtcNow.Ticks;

            var (placeOrders, cancelOrders) = GetGridBuyOrderUpdates();
            
            if (triggeringOrder != null)
            {
                var (placeRequests, cancelRequests) = await GetGridSellOrdersAsync(triggeringOrder);
                placeOrders = placeOrders.Concat(placeRequests);
                cancelOrders = cancelOrders.Concat(cancelRequests);
            }

            await UpdateOrdersAndOrderStates(placeOrders, cancelOrders);

            _log.LogInformation("{Symbol} - Update Grid Finished. Next update earliest: {PossibleNextTickTime:s}", _tradedSymbol.Symbol, DateTime.UtcNow.AddTicks(_ticksUpdateGridDelay));
        }
        finally
        {
            _orderLock.Release();
        }
    }

    private (IEnumerable<OKXOrderPlaceRequest>, IEnumerable<OKXOrderCancelRequest>) GetGridBuyOrderUpdates()
    {
        var gridLinesBuy = GridCalculator.GetGridBuyLinesAndPrices(_currentPrice, _tradedSymbol.TakeProfitPercent, _tradedSymbol.MaxActiveBuyOrders).ToArray();
        return (GetBuyOrderPlaceRequests(gridLinesBuy), GetCancelRequestsForOutdatedBuyOrders(gridLinesBuy.Min(e => e.Index)));
    }

    private async Task<(IEnumerable<OKXOrderPlaceRequest>, IEnumerable<OKXOrderCancelRequest>)> GetGridSellOrdersAsync(OKXOrderUpdate? triggeringOrder)
    {
        List<OKXOrderPlaceRequest> orderRequests = new();

        if (triggeringOrder != null)
        {
            var newSellOrder = await GetSellOrderForBuyOrderAsync(triggeringOrder);
            if (newSellOrder != null)
            {
                orderRequests.Add(newSellOrder);
                _log.LogDebug("{Symbol} - Placing SELL order {SellOrderId} for filled buy order {BuyOrderId}", _tradedSymbol.Symbol, ((GridLineOrderId)newSellOrder.ClientOrderId!).LineIndex, ((GridLineOrderId)triggeringOrder.ClientOrderId!).LineIndex);
            }
        }

        var (placeRequests, cancelRequests) = GetSellOrdersForOpenPosition(orderRequests);

        orderRequests.AddRange(placeRequests);

        return (orderRequests.DistinctBy(r => r.Price), cancelRequests);
    }

    private IEnumerable<OKXOrder> GetActiveOrders(OKXOrderSide? orderSide, bool includePartialFilled = false)
    {
        var activeOrders = _okx.GetActiveOrders(_tradedSymbol.SymbolType, _tradedSymbol.Symbol)
            .Where(o => o.OrderState == OKXOrderState.Live || (includePartialFilled && o.OrderState == OKXOrderState.PartiallyFilled));

        if (orderSide.HasValue)
        {
            activeOrders = activeOrders.Where(o => o.OrderSide == orderSide);
        }
        return activeOrders;
    }

    private int CountActiveOrders(OKXOrderSide? orderSide, bool includePartialFilled = false)
    {
        return GetActiveOrders(orderSide, includePartialFilled).Count();
    }

    private async Task<OKXOrderPlaceRequest?> GetSellOrderForBuyOrderAsync(OKXOrderUpdate triggeringOrder)
    {
        if (triggeringOrder.OrderSide != OKXOrderSide.Buy) return null;

        if (!GridLineOrderId.TryParse(triggeringOrder.ClientOrderId, out var gridOrderId))
        {
            _log.LogWarning("{Symbol} - Got an order without grid order id. Manually placed? OrderId: {OrderId}", _tradedSymbol.Symbol, triggeringOrder.OrderId);
            return null;
        }

        if (!triggeringOrder.PositionSide.HasValue)
        {
            _log.LogWarning("{Symbol} - Unknown position side of triggering order. OrderId: {OrderId}", _tradedSymbol.Symbol, triggeringOrder.OrderId);
            return null;
        }

        if (triggeringOrder.OrderState != OKXOrderState.Filled)
        {
            return null;
        }

        var sellGridLine = gridOrderId.LineIndex + 1;

        if (_gridLineOrderStates.TryGetValue(sellGridLine, out var state) 
            && state is OKXOrderState.Live or OKXOrderState.PartiallyFilled)
        {
            return null;
        }

        var sellPrice = GetPositionPrice(sellGridLine);
        var newGridOrderId = GridLineOrderId.Create(sellGridLine, OKXOrderSide.Sell);

        var orderRequest = new OKXOrderPlaceRequest
        {
            Symbol = triggeringOrder.Symbol,
            ClientOrderId = newGridOrderId,
            OrderSide = OKXOrderSide.Sell,
            OrderType = triggeringOrder.OrderType,
            PositionSide = triggeringOrder.PositionSide.Value,
            ReduceOnly = true,
            Quantity = triggeringOrder.Quantity,
            Price = sellPrice,
            TradeMode = triggeringOrder.TradeMode
        };

        return orderRequest;
    }

    private (IEnumerable<OKXOrderPlaceRequest>, IEnumerable<OKXOrderCancelRequest>) GetSellOrdersForOpenPosition(IEnumerable<OKXOrderPlaceRequest> orderRequests)
    {
        if (_position == null || _position.PositionsQuantity.GetValueOrDefault() == 0 || _position.AveragePrice.GetValueOrDefault() == 0)
        {
            return (Enumerable.Empty<OKXOrderPlaceRequest>(), Enumerable.Empty<OKXOrderCancelRequest>());
        }
        var inactiveQuantity = _activeQuantity;

        if (inactiveQuantity <= 0)
        {
            return (Enumerable.Empty<OKXOrderPlaceRequest>(), Enumerable.Empty<OKXOrderCancelRequest>());
        }

        var desiredStateOrderList = new List<OKXOrderPlaceRequest>();
        
        var sellGridLine = GetActiveOrders(OKXOrderSide.Buy, true).Select(o => o.GetGridLineIndex()).Max() + 2;

        while (inactiveQuantity > 0)
        {
            if (_lastSellOrderFilled?.GetGridLineIndex() == sellGridLine)
            {
                _log.LogDebug("{Symbol} - Skipping SELL order with id {SellOrderId}, recently filled at same line.", _tradedSymbol.Symbol, sellGridLine);
                ++sellGridLine;
                continue;
            }

            var gridPrice = GetPositionPrice(GridCalculator.GetGridPrice(_tradedSymbol.TakeProfitPercent, sellGridLine));

            var orderQuantity = GetPositionQuantity(gridPrice);
            if (inactiveQuantity - orderQuantity < 0)
            {
                orderQuantity = inactiveQuantity;
            }

            desiredStateOrderList.Add(new OKXOrderPlaceRequest
            {
                Symbol = _tradedSymbol.Symbol,
                TradeMode = OKXTradeMode.Cross,
                ClientOrderId = GridLineOrderId.Create(sellGridLine, OKXOrderSide.Buy),
                OrderSide = OKXOrderSide.Sell,
                PositionSide = OKXPositionSide.Long,
                OrderType = OKXOrderType.LimitOrder,
                Price = gridPrice,
                Quantity = orderQuantity,
                ReduceOnly = true
            });

            inactiveQuantity -= orderQuantity;
            ++sellGridLine;
        }

        var existingRequestLinedIndices = orderRequests.Select(o => o.GetGridLineIndex()).Distinct().ToArray();
        desiredStateOrderList = desiredStateOrderList.Where(o => !existingRequestLinedIndices.Contains(o.GetGridLineIndex())).Take(_tradedSymbol.MaxActiveSellOrders).ToList();
        
        var activeSellOrders = GetActiveOrders(OKXOrderSide.Sell, true).ToArray();
        var cancelList = activeSellOrders
            .Where(activeOrder => !desiredStateOrderList.Any(ds =>
                                      ds.GetGridLineIndex() == activeOrder.GetGridLineIndex())
                                  || desiredStateOrderList
                                      .Single(o =>
                                          o.GetGridLineIndex() == activeOrder.GetGridLineIndex())
                                      .Quantity != activeOrder.Quantity)
            .Select(order => new OKXOrderCancelRequest()
            {
                Symbol = order.Symbol,
                OrderId = order.OrderId.ToString()
            })
            .ToList();

        var placeRequests = desiredStateOrderList.Where(desiredOrder =>
            !activeSellOrders.Any(activeOrder => activeOrder.GetGridLineIndex() == desiredOrder.GetGridLineIndex()) 
            && !cancelList.Any(cancelRequest => cancelRequest.GetGridLineIndex() == desiredOrder.GetGridLineIndex())
        ).ToList();

        _log.LogDebug("{Symbol} - Desired State Sell Orders:", _tradedSymbol.Symbol);
        _log.LogDebug("{Symbol} - DESIRED: {DesiredOrders}", _tradedSymbol.Symbol, desiredStateOrderList.Select(o => (o.GetGridLineIndex(), o.Quantity)).OrderBy(o => o.Item1));
        _log.LogDebug("{Symbol} -  ACTIVE: {ActiveOrders}", _tradedSymbol.Symbol, activeSellOrders.Select(o => (o.GetGridLineIndex(), o.Quantity)).OrderBy(o => o.Item1));
        _log.LogDebug("{Symbol} -  CANCEL: {DesiredOrders}", _tradedSymbol.Symbol, cancelList.Select(o => o.GetGridLineIndex()).Order());

        return (placeRequests, cancelList);
    }

    private async Task UpdateOrdersAndOrderStates(IEnumerable<OKXOrderPlaceRequest> placeRequests, IEnumerable<OKXOrderCancelRequest> cancelRequests)
    {
        var placeRequestsArr = placeRequests.ToArray();
        var cancelRequestsArr = cancelRequests.ToArray();
        if (placeRequestsArr.Length <= 0 && cancelRequestsArr.Length <= 0) return;
        
        var numBuyOrders = placeRequestsArr.Count(o => o.OrderSide == OKXOrderSide.Buy);
        var numSellOrders = placeRequestsArr.Count(o => o.OrderSide == OKXOrderSide.Sell);
        _log.LogDebug("{Symbol} - Cancelling orders: {CancelOrderCount} - Placing orders: {BuyOrderCount} BUY, {SellOrderCount} SELL", _tradedSymbol.Symbol, cancelRequestsArr.Length, numBuyOrders, numSellOrders);

        await _okx.SetCrossLeverage(_tradedSymbol.Leverage, _tradedSymbol.Symbol);

        if (cancelRequestsArr.Length > 0)
        {
            var cancelResults = await _okx.CancelOrdersAsync(cancelRequestsArr).ToArrayOrEmptyAsync();
            if (cancelResults.Any(r => r.Code != "0"))
            {
                _log.LogWarning("{Symbol} - Error cancelling orders: {messages}", _tradedSymbol.Symbol,
                    cancelResults.Where(r => r.Code != "0").Select(r => r.Message));
            }

            UpdateOrderStatesFromCancelResponses(cancelResults);
        }

        if (placeRequestsArr.Length > 0)
        {
            var placeResults = await _okx.PlaceOrdersAsync(placeRequestsArr).ToArrayOrEmptyAsync();
            var successfulOrders = new List<OKXOrderPlaceResponse>();
            foreach (var placeResponse in placeResults)
            {
                if (placeResponse.Code != "0")
                {
                    _log.LogWarning("{Symbol} - Error placing order: {message}", _tradedSymbol.Symbol, placeResponse.Message);
                    continue;
                }

                successfulOrders.Add(placeResponse);
            }

            Interlocked.Exchange(ref _lastPlaceResponses, successfulOrders.ToArray());

            UpdateOrderStatesFromPlaceResponses(placeResults);
        }
    }

    private IEnumerable<OKXOrderCancelRequest> GetCancelRequestsForOutdatedBuyOrders(decimal lowestValidIndex)
    {
        var activeOrders = _okx.GetActiveOrders(_tradedSymbol.SymbolType, _tradedSymbol.Symbol);
        var activeBuyOrders = activeOrders.Where(o => o is { OrderSide: OKXOrderSide.Buy, OrderState: OKXOrderState.Live }).ToArray();
        
        var belowValidIndexOrders = activeBuyOrders.Where(o => !string.IsNullOrWhiteSpace(o.ClientOrderId) && GridLineOrderId.TryParse(o.ClientOrderId, out var gridOrderId) && gridOrderId.LineIndex < lowestValidIndex);
        var tooManyOrders = activeBuyOrders.OrderByDescending(o => o.Price).Skip(_tradedSymbol.MaxActiveBuyOrders);
        
        var cancelRequests = belowValidIndexOrders.Concat(tooManyOrders)
            .DistinctBy(o => o.OrderId)
            .Select(o => new OKXOrderCancelRequest
            {
                OrderId = o.OrderId.ToString(),
                ClientOrderId = o.ClientOrderId,
                Symbol = o.Symbol
            }).ToArray();

        return cancelRequests;
    }

    private OKXOrderPlaceRequest[] GetBuyOrderPlaceRequests(IEnumerable<(int Index, decimal Price)> gridLines)
    {
        var numActiveBuyOrders = CountActiveOrders(OKXOrderSide.Buy);
        var numOrdersToCreate = _tradedSymbol.MaxActiveBuyOrders - numActiveBuyOrders;
        if (numOrdersToCreate < 1)
        {
            return Array.Empty<OKXOrderPlaceRequest>();
        }

        var orders = gridLines
            .Where(gridLine => !HasActiveOrderForLine(gridLine.Index))
            .Select(gridLine =>
            {
                var positionPrice = GetPositionPrice(gridLine.Price);
                var positionQuantity = GetPositionQuantity(positionPrice);
                var request = new OKXOrderPlaceRequest
                {
                    Symbol = _tradedSymbol.Symbol,
                    TradeMode = OKXTradeMode.Cross,
                    ClientOrderId = GridLineOrderId.Create(gridLine.Index, OKXOrderSide.Buy),
                    OrderSide = OKXOrderSide.Buy,
                    PositionSide = OKXPositionSide.Long,
                    OrderType = OKXOrderType.LimitOrder,
                    Price = positionPrice,
                    Quantity = positionQuantity,
                };
                return request;
            });
        return orders.ToArray();
    }

    private bool HasActiveOrderForLine(int lineIndex) =>
        _gridLineOrderStates.Any(o =>
            o.Key == lineIndex && o.Value is OKXOrderState.Live or OKXOrderState.PartiallyFilled);

    private void UpdateOrderStatesFromPlaceResponses(IEnumerable<OKXOrderPlaceResponse>? responses)
    {
        foreach (var response in (responses ?? Enumerable.Empty<OKXOrderPlaceResponse>()).Where(r => r.Code == "0"))
        {
            if (!GridLineOrderId.TryParse(response.ClientOrderId, out var gridOrderId))
            {
                _log.LogWarning("{Symbol} - Sent grid orders, response came back without grid line order id. order ID: {orderId}", _tradedSymbol.Symbol, response.OrderId);
                continue;
            }

            if (response.OrderId.HasValue)
            {
                _gridLineOrderStates[gridOrderId.LineIndex] = OKXOrderState.Live;
            }
        }
    }

    private void UpdateOrderStatesFromCancelResponses(IEnumerable<OKXOrderCancelResponse>? responses)
    {
        foreach (var response in (responses ?? Enumerable.Empty<OKXOrderCancelResponse>()).Where(r => r.Code == "0"))
        {
            if (!GridLineOrderId.TryParse(response.ClientOrderId, out var gridOrderId))
            {
                _log.LogWarning("{Symbol} - Sent grid orders, response came back without grid line order id. order ID: {orderId}", _tradedSymbol.Symbol, response.OrderId);
                continue;
            }

            _gridLineOrderStates[gridOrderId.LineIndex] = OKXOrderState.Canceled;
        }
    }

    private decimal GetPositionPrice(int gridLineIndex) => 
        GetPositionPrice(GridCalculator.GetGridPrice(_tradedSymbol.TakeProfitPercent, gridLineIndex));

    private decimal GetPositionPrice(decimal price)
    {
        if (_instrument == null)
        {
            throw new InvalidOperationException("Instrument not found.");
        }

        return Math.Round(price, _instrument.TickSize.Scale, MidpointRounding.ToZero);
    }

    private decimal GetPositionQuantity(decimal price)
    {
        if (_instrument == null)
        {
            throw new InvalidOperationException("Instrument not found.");
        }

        if (!_instrument.ContractValue.HasValue)
        {
            throw new InvalidOperationException("Cannot calculate quantity for instrument without contract value.");
        }

        var contractValue = _instrument.ContractValue.Value * price;
        var leveragedAmount = _tradedSymbol.InvestPerGrid * _tradedSymbol.Leverage;
        var quantity = Math.Round(leveragedAmount / contractValue, _instrument.LotSize.Scale, MidpointRounding.ToEven);
        if (quantity < _instrument.LotSize) quantity = _instrument.LotSize;
        if (quantity % _instrument.LotSize != 0)
        {
            quantity -= quantity % _instrument.LotSize;
        }
        return quantity;
    }

    public override void Dispose()
    {
        base.Dispose();

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _orderFixTimer?.Dispose();
        _checkLastUpdateTimer?.Dispose();
    }
}
