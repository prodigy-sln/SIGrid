using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Channels;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OKX.Net.Clients;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using OKX.Net.Objects.Market;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.App.GridBot.Extensions;
using SIGrid.App.GridBot.Helper;

namespace SIGrid.App.GridBot.OKX;

public class OKXConnector : IAsyncDisposable
{
    private static readonly int MaxBulkRequestSize = 20;

    private readonly IOptions<SIGridOptions> _gridBotOptions;
    private readonly OKXRestClient _restClient;
    private readonly OKXSocketClient _socketClient;
    private readonly ILogger<OKXConnector> _log;
    private readonly List<UpdateSubscription> _socketSubscriptions = new();
    private readonly ConcurrentDictionary<long, OKXPosition> _position = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, OKXOrder>> _orders = new();
    private readonly ConcurrentDictionary<OKXInstrumentType, List<OKXInstrument>> _symbols = new();
    private readonly ConcurrentDictionary<string, IObservable<OKXTicker>> _symbolTickerObservables = new();
    private readonly ConcurrentDictionary<string, decimal> _currentPrices = new();

    private CancellationTokenSource _cts = null!;
    private IObservable<OKXPosition> _positionUpdateObservable = null!;
    private IObservable<OKXOrderUpdate> _orderUpdateObservable = null!;

    public OKXAccountBalance AccountBalance { get; set; } = null!;

    public OKXConnector(IOptions<SIGridOptions> gridBotOptions, OKXRestClient restClient, OKXSocketClient socketClient, ILogger<OKXConnector> log)
    {
        _gridBotOptions = gridBotOptions;
        _restClient = restClient;
        _socketClient = socketClient;
        _log = log;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await UpdateSymbolsAsync(ct);
        await UpdateBalancesAsync(ct);
        await UpdateOrdersAsync(ct);

        SubscribeBackgroundUpdates(_cts.Token);
    }

    public OKXInstrument? GetSymbol(string symbolType, string symbol)
    {
        var instrumentType = symbolType.ToOKXInstrumentTypeOrException();
        return _symbols[instrumentType]
            .Find(s => s.Symbol.Equals(symbol, StringComparison.InvariantCultureIgnoreCase));
    }

    public async Task<OKXFeeRate?> GetFeeRateAsync(OKXInstrument instrument)
    {
        var result = await ApiCallHelper.ExecuteWithRetry(
            () => _restClient.UnifiedApi.Account.GetFeeRatesAsync(
                instrument.InstrumentType,
                symbol: instrument.Symbol,
                underlying: instrument.Underlying));

        if (result.GetResultOrError(out var data, out var error)) return data;

        _log.LogError("Error loading fee rates: {message}", error.Message);
        return null;
    }

    public IObservable<OKXTicker> GetSymbolTickerSubscription(OKXInstrument instrument, CancellationToken ct)
    {
        return GetOrCreateSymbolTickerObservable(instrument, ct);
    }

    public IObservable<OKXOrder> GetOrderUpdateSubscription(OKXInstrument instrument)
    {
        return _orderUpdateObservable.Where(o =>
            o.InstrumentType == instrument.InstrumentType && o.Symbol.Equals(instrument.Symbol));
    }

    public IObservable<OKXPosition> GetPositionUpdateSubscription(OKXInstrument instrument)
    {
        return _positionUpdateObservable.Where(p =>
            p.InstrumentType == instrument.InstrumentType && p.Symbol == instrument.Symbol);
    }

    public async Task<decimal> GetCurrentPriceAsync(OKXInstrument instrument, CancellationToken ct)
    {
        if (_currentPrices.TryGetValue(instrument.Symbol, out var currentPrice)) return currentPrice;

        var ticker = await ApiCallHelper.ExecuteWithRetry(
            () => _restClient.UnifiedApi.ExchangeData.GetTickerAsync(instrument.Symbol, ct), ct: ct);

        return ticker.GetResultOrError(out var data, out _)
            ? data.LastPrice.GetValueOrDefault()
            : 0.0M;
    }

    public IEnumerable<OKXOrder> GetCurrentOrders(OKXInstrument instrument)
    {
        return GetActiveOrders(instrument)
            .Where(o => o.GetGridLineIndex() > 0)
            .Where(o => o.InstrumentType == instrument.InstrumentType && o.Symbol == instrument.Symbol);
    }

    public async Task<IEnumerable<OKXOrderPlaceResponse>?> PlaceOrdersAsync(IEnumerable<OKXOrderPlaceRequest> placeRequests)
    {
        var orderPlaceRequests = placeRequests as OKXOrderPlaceRequest[] ?? placeRequests.ToArray();

        if (orderPlaceRequests.Length == 0) return Enumerable.Empty<OKXOrderPlaceResponse>();

        if (orderPlaceRequests.Length > MaxBulkRequestSize)
        {
            var results = await Task.WhenAll(orderPlaceRequests
                .Slice(MaxBulkRequestSize)
                .Select(PlaceOrdersAsync));
            return results.WhereNotNull().SelectMany(r => r);
        }

        var result = await _socketClient.UnifiedApi.Trading.PlaceMultipleOrdersAsync(orderPlaceRequests);
        //var result = await _restClient.UnifiedApi.Trading.PlaceMultipleOrdersAsync(placeRequests);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error placing orders: {message}", error.Message);
            return null;
        }

        var dataArr = data.ToArray();

        foreach (var response in dataArr.Where(r => r.Code != "0")
                     .ToDictionary(k => k,
                         v => orderPlaceRequests
                             .Single(r => r.ClientOrderId == v.ClientOrderId)
                     ))
        {
            _log.LogWarning("{Symbol} - Error placing order: {Message}", response.Value.Symbol, response.Key.Message);
        }

        return dataArr;
    }

    public async Task<IEnumerable<OKXOrderCancelResponse>?> CancelOrdersAsync(IEnumerable<OKXOrderCancelRequest> cancelRequests, bool useApi = false)
    {
        var ordersToCancel = cancelRequests as OKXOrderCancelRequest[] ?? cancelRequests.ToArray();

        if (ordersToCancel.Length == 0) return Enumerable.Empty<OKXOrderCancelResponse>();

        if (ordersToCancel.Length > MaxBulkRequestSize)
        {
            return (await Task.WhenAll(ordersToCancel.Slice(MaxBulkRequestSize).Select(g => CancelOrdersAsync(g))))
                .WhereNotNull().SelectMany(r => r);
        }

        var result = useApi
            ? await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Trading.CancelMultipleOrdersAsync(ordersToCancel))
            : await _socketClient.UnifiedApi.Trading.CancelMultipleOrdersAsync(ordersToCancel);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error cancelling orders: {message}", error.Message);
            return data;
        }

        var dataArr = data.ToArray();

        foreach (var response in dataArr
                     .Where(r => r.Code != "0")
                     .ToDictionary(k => k,
                         v => ordersToCancel
                             .Single(r => r.OrderId == v.OrderId.ToString())
                     ))
        {
            _log.LogWarning("{Symbol} - Error cancelling order: {Message}", response.Value.Symbol, response.Key.Message);
        }

        return dataArr;
    }

    public async Task<IEnumerable<OKXPosition>> GetOpenPositionsAsync(OKXInstrument instrument)
    {
        if (instrument.InstrumentType == OKXInstrumentType.Spot)
        {
            return Enumerable.Empty<OKXPosition>();
        }

        var positionsResult = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.GetAccountPositionsAsync(instrument.InstrumentType, instrument.Symbol));
        if (!positionsResult.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error loading positions for symbol {Symbol}: {message}", instrument.Symbol, error.Message);
            return Enumerable.Empty<OKXPosition>();
        }

        return data;
    }

    public async Task<OKXTicker?> GetTickerAsync(OKXInstrument instrument)
    {
        var ticker = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.ExchangeData.GetTickerAsync(instrument.Symbol));
        if (!ticker.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error loading ticket data for symbol {Symbol}: {message}", instrument.Symbol, error.Message);
            return null;
        }

        return data;
    }

    private static string GetInstrumentKey(OKXInstrument instrument) => 
        $"{instrument.InstrumentType}_{instrument.Symbol}";

    private static string GetInstrumentKey(OKXOrder order) =>
        $"{order.InstrumentType}_{order.Symbol}";

    private void SubscribeBackgroundUpdates(CancellationToken ct)
    {
        Observable.Timer(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15))
            .Select(_ => Observable.FromAsync(UpdateBalancesAsync)).Concat().Subscribe(ct);
        Observable.Timer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
            .Select(_ => Observable.FromAsync(UpdateSymbolsAsync)).Concat().Subscribe(ct);
        Observable.Timer(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30))
            .Select(_ => Observable.FromAsync(UpdateOrdersAsync)).Concat().Subscribe(ct);

        CreatePositionUpdateObservableAsync(ct);
        CreateOrderUpdateObservableAsync(ct);
    }

    private void CreatePositionUpdateObservableAsync(CancellationToken ct)
    {
        _positionUpdateObservable = Observable.Create<OKXPosition>(o => GetSubscriptionCallbackAsObservable(o,
            async onNext =>
                await _socketClient.UnifiedApi.Trading.SubscribeToPositionUpdatesAsync(OKXInstrumentType.Any, null,
                    null, true,
                    onNext, ct),
            position =>
            {
                if (!position.PositionId.HasValue) return;

                _position[position.PositionId.Value] = position;
            }, ct: ct));
    }

    private void CreateOrderUpdateObservableAsync(CancellationToken ct)
    {
        _orderUpdateObservable = Observable.Create<OKXOrderUpdate>(o => GetSubscriptionCallbackAsObservable(o,
            async onNext =>
                await _socketClient.UnifiedApi.Trading.SubscribeToOrderUpdatesAsync(OKXInstrumentType.Any,
                    null, null,
                    onNext, ct),
            orderUpdate =>
            {
                if (string.IsNullOrWhiteSpace(orderUpdate.ClientOrderId)) return;

                if (orderUpdate.OrderState is OKXOrderState.Filled or OKXOrderState.Canceled)
                {
                    RemoveActiveOrder(orderUpdate);
                }
                else
                {
                    AddOrUpdateActiveOrder(orderUpdate);
                }
            }, ct: ct));
    }

    private void AddOrUpdateActiveOrder(OKXOrder order)
    {
        if (order.OrderId is null or 0) return;

        var activeOrders = GetActiveOrdersForKey(GetInstrumentKey(order));
        activeOrders.AddOrUpdate(order.OrderId.Value, order, (_, _) => order);
    }

    private void AddOrUpdateActiveOrders(IEnumerable<OKXOrder> orders)
    {
        var tempDictionary = new ConcurrentDictionary<string, ConcurrentDictionary<long, OKXOrder>>();
        foreach (var orderGroup in orders.Where(o => o.OrderId > 0).GroupBy(GetInstrumentKey))
        {
            tempDictionary[orderGroup.Key] = new ConcurrentDictionary<long, OKXOrder>();
            foreach (var order in orderGroup.Where(o => o.OrderId.HasValue && o.OrderId != 0))
            {
                tempDictionary[orderGroup.Key].AddOrUpdate(order.OrderId!.Value, order, (_, _) => order);
            }
            _orders[orderGroup.Key] = tempDictionary[orderGroup.Key];
        }
    }

    private void RemoveActiveOrder(OKXOrder order)
    {
        if (order.OrderId is null or 0) return;

        var activeOrders = GetActiveOrdersForKey(GetInstrumentKey(order));
        activeOrders.Remove(order.OrderId.Value, out _);
    }

    private OKXOrder[] GetActiveOrders(OKXInstrument instrument)
    {
        return GetActiveOrdersForKey(GetInstrumentKey(instrument)).Values.ToArray();
    }

    private ConcurrentDictionary<long, OKXOrder> GetActiveOrdersForKey(string key)
    {
        return _orders.TryGetValue(key, out var orders) ? orders : _orders[key] = new ConcurrentDictionary<long, OKXOrder>();
    }

    private IObservable<OKXTicker> GetOrCreateSymbolTickerObservable(OKXInstrument instrument, CancellationToken ct)
    {
        return _symbolTickerObservables.GetOrAdd(instrument.Symbol, _ =>
            Observable.Create<OKXTicker>(o =>
                GetSubscriptionCallbackAsObservable(o, async onNext =>
                        await _socketClient.UnifiedApi.ExchangeData.SubscribeToTickerUpdatesAsync(instrument.Symbol,
                            onNext, ct),
                    ticker =>
                    {
                        if (!ticker.LastPrice.HasValue) return;

                        _currentPrices[ticker.Symbol] = ticker.LastPrice.Value;
                    },
                    ct: ct)));
    }

    private async Task UpdateSymbolsAsync(CancellationToken ct)
    {
        await Task.WhenAll(Enum.GetValues<OKXInstrumentType>()
            .Where(t => t != OKXInstrumentType.Any && t != OKXInstrumentType.Option && t != OKXInstrumentType.Contracts)
            .Select(UpdateInstrumentTypeSymbolsAsync));
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

    private void UpdateSymbolsInternal(OKXInstrumentType instrumentType, IEnumerable<OKXInstrument> data)
    {
        if (!_symbols.TryGetValue(instrumentType, out var list))
        {
            _symbols[instrumentType] = list = new List<OKXInstrument>();
        }

        list.Clear();
        list.AddRange(data);
    }

    private async Task UpdateBalancesAsync(CancellationToken ct)
    {
        var result = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Account.GetAccountBalanceAsync(ct: ct), ct: ct);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error updating balances: {message}", error.Message);
            return;
        }

        AccountBalance = data;
        _log.LogTrace("Balance Updated. Total Equity: {totalEquity:#.000}", AccountBalance.TotalEquity);
    }

    private async Task UpdateOrdersAsync(CancellationToken ct)
    {
        _log.LogTrace("Updating active orders.");
        foreach (var tradedSymbol in _gridBotOptions.Value.TradedSymbols)
        {
            await UpdateOrdersForSymbolAsync(tradedSymbol, ct);
        }
    }

    private async Task UpdateOrdersForSymbolAsync(SIGridOptions.TradedSymbolOptions tradedSymbol, CancellationToken ct, long? beforeOrderId = null)
    {
        var instrumentType = Enum.Parse<OKXInstrumentType>(tradedSymbol.SymbolType);
        var activeOrders = await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Trading.GetOrdersAsync(instrumentType, symbol: tradedSymbol.Symbol, before: beforeOrderId, limit: 100, ct: ct), ct: ct);
        if (!activeOrders.GetResultOrError(out var data, out var error))
        {
            _log.LogWarning("{Symbol} - Error loading orders: {message}", tradedSymbol.Symbol, error);
            return;
        }

        var dataArr = data.ToArray();

        AddOrUpdateActiveOrders(dataArr);

        if (dataArr.Length == 100)
        {
            await UpdateOrdersForSymbolAsync(tradedSymbol, ct, dataArr.Where(o => o.OrderId.HasValue).Select(o => o.OrderId).Append(long.MaxValue).Min());
        }
    }

    private async Task GetSubscriptionCallbackAsObservable<T>(IObserver<T> observer, 
        Func<Action<T>,
        Task<CallResult<UpdateSubscription>>> subscriptionFactory,
        Action<T> callback,
        int maxQueuedItems = 10, 
        CancellationToken ct = default)
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
                callback(next);
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
            channel.Writer.TryWrite(next);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _socketSubscriptions)
        {
            await subscription.CloseAsync();
        }

        await CastAndDispose(_restClient);
        await CastAndDispose(_socketClient);
        await CastAndDispose(_cts);

        GC.SuppressFinalize(this);

        return;

        static async ValueTask CastAndDispose(IDisposable resource)
        {
            if (resource is IAsyncDisposable resourceAsyncDisposable)
                await resourceAsyncDisposable.DisposeAsync();
            else
                resource.Dispose();
        }
    }
}
