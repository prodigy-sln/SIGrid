using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Channels;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using DynamicData;
using Lamar;
using Microsoft.Extensions.Logging;
using OKX.Net.Clients;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using OKX.Net.Objects.Market;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.Exchange.Shared;
using SIGrid.Exchange.Shared.Extensions;
using SIGrid.TradeBot.Extensions;

namespace SIGrid.TradeBot;

[Singleton]
public class OKXExchangeConnector : IAsyncDisposable
{
    private readonly ILogger<OKXExchangeConnector> _log;

    private readonly SourceCache<OKXInstrument, (OKXInstrumentType, string)> _symbolsCache = new(s => (s.InstrumentType, s.Symbol));

    private readonly SourceCache<OKXOrder, string> _ordersCache = new(s => s.ClientOrderId!);

    private readonly SourceCache<OKXPosition, long> _positionCache = new(s => s.PositionId!.Value);

    private readonly OKXRestClient _restClient;

    private readonly OKXSocketClient _socketClient;

    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentBag<UpdateSubscription> _socketSubscriptions = new();

    private readonly ConcurrentDictionary<string, IObservable<OKXTicker>> _symbolTickerObservables = new();

    private readonly ConcurrentDictionary<string, IObservable<OKXPosition>> _positionObservables = new();

    private readonly ConcurrentDictionary<string, IObservable<OKXOrderUpdate>> _orderObservables = new();

    private IObservable<OKXOrderUpdate>? _orderUpdateObservable;

    private IObservable<OKXPosition>? _positionUpdateObservable;

    private readonly ManualResetEventSlim _loadedLock = new(false);

    private long _currentGridLine;

    public OKXAccountBalance AccountBalance { get; private set; }

    public OKXExchangeConnector(OKXRestClient restClient, OKXSocketClient socketClient, ILogger<OKXExchangeConnector> log)
    {
        _log = log;
        _restClient = restClient;
        _socketClient = socketClient;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(15))
            .Select(_ => Observable.FromAsync(UpdateBalancesAsync)).Concat().Subscribe(cts.Token);
        Observable.Timer(TimeSpan.Zero, TimeSpan.FromSeconds(60))
            .Select(_ => Observable.FromAsync(UpdateSymbolsAsync)).Concat().Subscribe(cts.Token);
        _orderUpdateObservable = Observable.Create<OKXOrderUpdate>(o => GetSubscriptionCallbackAsObservable(o,
            async onNext =>
                await _socketClient.UnifiedApi.Trading.SubscribeToOrderUpdatesAsync(OKXInstrumentType.Any, null, null,
                    onNext, cts.Token), ct: cts.Token))
            .Where(o => !string.IsNullOrWhiteSpace(o.ClientOrderId))
            .Select(orderUpdate =>
            {
                _ordersCache.AddOrUpdate(orderUpdate);
                return orderUpdate;
            });
        _positionUpdateObservable = Observable.Create<OKXPosition>(o => GetSubscriptionCallbackAsObservable(o,
                async onNext =>
                    await _socketClient.UnifiedApi.Trading.SubscribeToPositionUpdatesAsync(OKXInstrumentType.Any, null, null, true,
                        onNext, cts.Token), ct: cts.Token))
            .Select(position =>
            {
                if (position.PositionId.HasValue)
                    _positionCache.AddOrUpdate(position);
                return position;
            });
    }

    public async Task<OKXInstrument?> GetSymbolInfo(string symbolType, string symbol)
    {
        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();
        var symbolInfo = _symbolsCache.Items.FirstOrDefault(s => s.InstrumentType == instrumentType && s.Symbol == symbol);
        if (symbolInfo != null)
        {
            return symbolInfo;
        }

        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.ExchangeData.GetSymbolsAsync(instrumentType, symbol: symbol));
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error loading symbol info: {message}", error.Message);
            return null;
        }

        var instruments = data.ToArray();
        _symbolsCache.AddOrUpdate(instruments);
        return instruments.FirstOrDefault(i => i.InstrumentType == instrumentType && i.Symbol == symbol);
    }

    public async Task<OKXFeeRate?> GetFeeRateAsync(OKXInstrument instrument)
    {
        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.GetFeeRatesAsync(instrument.InstrumentType, underlying: instrument.Underlying));
        if (!result.GetResultOrError(out var data, out var error))
        {
            if (error.Code == 50011)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(250, 1001)));
                return await GetFeeRateAsync(instrument);
            }
            _log.LogError("Error loading fee rates: {message}", error.Message);
            return null;
        }

        return data;
    }

    public IObservable<OKXInstrument> GetSymbolInfoSubscription()
    {
        return _symbolsCache.Connect(_ => true).SelectMany(s => _symbolsCache.Items);
    }

    public async Task SetCrossLeverage(decimal leverage, string symbol)
    {
        var currentLeverageResult = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.GetAccountLeverageAsync(symbol, OKXMarginMode.Cross));
        if (!currentLeverageResult.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error reading current leverage: {message}", error.Message);
        }

        if ((data?.Any(l => l.Symbol == symbol && l.Leverage == leverage && l.MarginMode == OKXMarginMode.Cross)).GetValueOrDefault())
        {
            return;
        }

        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.SetAccountLeverageAsync((int)leverage, OKXMarginMode.Cross, null, symbol));
        if (!result.GetResultOrError(out data, out error))
        {
            _log.LogError("Error setting leverage: {message}", error.Message);
        }
    }

    public async Task<IEnumerable<OKXOrderPlaceResponse>?> PlaceOrdersAsync(IEnumerable<OKXOrderPlaceRequest> orders)
    {
        var result = await _socketClient.UnifiedApi.Trading.PlaceMultipleOrdersAsync(orders);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error placing orders: {message}", error.Message);
            return null;
        }

        return data;
    }

    public async Task<IEnumerable<OKXOrderCancelResponse>?> CancelOrdersAsync(IEnumerable<OKXOrderCancelRequest> cancelRequests)
    {
        var result = await _socketClient.UnifiedApi.Trading.CancelMultipleOrdersAsync(cancelRequests);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error cancelling orders: {message}", error.Message);
            return data;
        }

        var dataArr = data.ToArray();

        _ordersCache.RemoveKeys(dataArr.Where(o => !string.IsNullOrWhiteSpace(o.ClientOrderId)).Select(o => o.ClientOrderId!));

        return dataArr;
    }

    public OKXOrder[] GetActiveOrders(string symbolType, string symbol)
    {
        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();
        return _ordersCache.Items.Where(o => o.InstrumentType == instrumentType && o.Symbol == symbol).ToArray();
    }

    public async Task<OKXOrder?> GetOrderUpdateByGridLine(string symbolType, string symbol, int gridLine)
    {
        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();

        var latestOrder = GetOrderByGridId();
        if (latestOrder != null)
        {
            return latestOrder;
        }

        var orderHistoryResult = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Trading.GetOrderHistoryAsync(instrumentType, symbol, state: OKXOrderState.Filled));
        if (!orderHistoryResult.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error loading order history: {message}", error.Message);
            return null;
        }
        _ordersCache.AddOrUpdate(data);

        return GetOrderByGridId();

        OKXOrder? GetOrderByGridId()
        {
            return _ordersCache.Items
                .Where(o => o.InstrumentType == instrumentType 
                            && o.Symbol == symbol
                            && o.OrderState == OKXOrderState.Filled 
                            && GridLineOrderId.TryParse(o.ClientOrderId, out var gridId) 
                            && gridId.LineIndex == gridLine)
                .MaxBy(o => o.FillTime);
        }
    }

    public async Task<IEnumerable<OKXOrder>> GetActiveOrdersAsync(string symbolType, string symbol)
    {
        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Trading.GetOrdersAsync(symbolType.ToOKXInstrumentTypeOrException(), symbol));
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error loading active orders: {message}", error.Message);
            return Enumerable.Empty<OKXOrder>();
        }

        var orders = data.ToArray();
        _ordersCache.AddOrUpdate(orders);
        return orders;
    }

    public IObservable<OKXTicker> GetSymbolTickerSubscription(string symbol, CancellationToken ct)
    {
        if (!_loadedLock.IsSet)
        {
            _loadedLock.Wait(ct);
        }

        return _symbolTickerObservables.GetOrAdd(symbol, _ =>
            Observable.Create<OKXTicker>(o =>
                GetSubscriptionCallbackAsObservable(o, async onNext =>
                        await _socketClient.UnifiedApi.ExchangeData.SubscribeToTickerUpdatesAsync(symbol, onNext, ct),
                    ct: ct)));
    }

    public IObservable<OKXPosition> GetPositionSubscription(string symbolType, string symbol, CancellationToken ct)
    {
        if (!_loadedLock.IsSet)
        {
            _loadedLock.Wait(ct);
        }

        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();
        var symbolInfo = _symbolsCache.Lookup((instrumentType, symbol));
        if (!symbolInfo.HasValue)
        {
            throw new ArgumentException("Symbol not found.", symbol);
        }

        if (_positionUpdateObservable != null)
        {
            return _positionUpdateObservable.Where(p => p.InstrumentType == instrumentType && p.Symbol == symbol);
        }

        throw new InvalidOperationException("Position subscription is null.");
    }

    public IObservable<OKXOrderUpdate> GetOrderSubscription(string symbolType, string symbol, CancellationToken ct)
    {
        if (!_loadedLock.IsSet)
        {
            _loadedLock.Wait(ct);
        }

        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();
        var symbolInfo = _symbolsCache.Lookup((instrumentType, symbol));
        if (!symbolInfo.HasValue)
        {
            throw new ArgumentException("Symbol not found.", symbol);
        }

        if (_orderUpdateObservable != null)
        {
            return _orderUpdateObservable.Where(o => o.InstrumentType == instrumentType && o.Symbol == symbol);
        }

        throw new InvalidOperationException("Order subscription is null.");
    }

    private async Task GetSubscriptionCallbackAsObservable<T>(IObserver<T> observer, Func<Action<T>, Task<CallResult<UpdateSubscription>>> subscriptionFactory, int maxQueuedItems = 10, CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(maxQueuedItems)
        {
            SingleReader = true,
            SingleWriter = true
        });

        var result = await subscriptionFactory(OnNext);
        if (!result.GetResultOrError(out var subscription, out var error))
        {
            throw new Exception($"Could not create subscription: {error.Message}");
        }

        _socketSubscriptions.Add(subscription);

        while (await channel.Reader.WaitToReadAsync(ct))
        {
            try
            {
                var next = await channel.Reader.ReadAsync(ct).ConfigureAwait(false);
                observer.OnNext(next);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error during observer execution.");
            }
        }

        observer.OnCompleted();

        void OnNext(T next)
        {
            channel!.Writer.TryWrite(next);
        }
    }

    private async Task UpdateSymbolsAsync()
    {
        await Task.WhenAll(Enum.GetValues<OKXInstrumentType>()
            .Where(t => t != OKXInstrumentType.Any && t != OKXInstrumentType.Option && t != OKXInstrumentType.Contracts)
            .Select(UpdateInstrumentTypeSymbolsAsync));
        _log.LogInformation("Found a total of {symbolsCount} symbols.", _symbolsCache.Count);
        
        if (!_loadedLock.IsSet)
        {
            _loadedLock.Set();
        }
    }

    private async Task UpdateInstrumentTypeSymbolsAsync(OKXInstrumentType instrumentType)
    {
        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.ExchangeData.GetSymbolsAsync(instrumentType));
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error while updating symbols: {message}", error.Message);
            return;
        }

        UpdateSymbolsInternal(instrumentType, data);
    }

    private void UpdateSymbolsInternal(OKXInstrumentType instrumentType, IEnumerable<OKXInstrument> instruments)
    {
        _symbolsCache.Edit(u =>
        {
            foreach (var existing in u.Items.Where(i => i.InstrumentType == instrumentType))
            {
                u.Remove(existing);
            }

            foreach (var symbol in instruments)
            {
                u.AddOrUpdate(symbol);
            }
        });
    }

    private async Task UpdateBalancesAsync()
    {
        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.GetAccountBalanceAsync());
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error updating balances: {message}", error.Message);
            return;
        }

        AccountBalance = data;
        _log.LogInformation("Balance Updated. Total Equity: {totalEquity:#.000}", AccountBalance.TotalEquity);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var subscription in _socketSubscriptions)
        {
            await _socketClient.UnsubscribeAsync(subscription);
        }
        _symbolsCache.Dispose();
        _restClient.Dispose();
        _socketClient.Dispose();
    }
}
