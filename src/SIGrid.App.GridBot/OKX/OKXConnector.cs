using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Threading.Channels;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Logging;
using OKX.Net.Clients;
using OKX.Net.Enums;
using OKX.Net.Objects.Account;
using OKX.Net.Objects.Market;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.App.GridBot.Extensions;

namespace SIGrid.App.GridBot.OKX;

public class OKXConnector
{
    private readonly OKXRestClient _restClient;
    private readonly OKXSocketClient _socketClient;
    private readonly ILogger<OKXConnector> _log;
    private readonly List<UpdateSubscription> _socketSubscriptions = new();
    private readonly ConcurrentDictionary<long, OKXPosition> _position = new();
    private readonly ConcurrentDictionary<string, OKXOrder> _orders = new();
    private readonly ConcurrentDictionary<OKXInstrumentType, List<OKXInstrument>> _symbols = new();
    private readonly ConcurrentDictionary<string, IObservable<OKXTicker>> _symbolTickerObservables = new();
    private readonly ConcurrentDictionary<string, decimal> _currentPrices = new();

    private CancellationTokenSource _cts;
    private IObservable<OKXPosition> _positionUpdateObservable;
    private IObservable<OKXOrderUpdate> _orderUpdateObservable;

    public OKXAccountBalance AccountBalance { get; set; }

    public OKXConnector(OKXRestClient restClient, OKXSocketClient socketClient, ILogger<OKXConnector> log)
    {
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
        return _orders.Values
            .Where(o => o.InstrumentType == instrument.InstrumentType && o.Symbol == instrument.Symbol);
    }

    public async Task<IEnumerable<OKXOrderPlaceResponse>?> PlaceOrdersAsync(IEnumerable<OKXOrderPlaceRequest> placeRequests)
    {
        var result = await _socketClient.UnifiedApi.Trading.PlaceMultipleOrdersAsync(placeRequests);
        if (!result.GetResultOrError(out var data, out var error))
        {
            _log.LogError("Error placing orders: {message}", error.Message);
            return null;
        }

        var dataArr = data.ToArray();

        foreach (var response in dataArr.Where(r => r.Code != "0"))
        {
            _log.LogWarning("Error placing order: {Message}", response.Message);
        }

        return dataArr;
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

        foreach (var response in dataArr.Where(r => r.Code != "0"))
        {
            _log.LogWarning("Error cancelling order: {Message}", response.Message);
        }

        return dataArr;
    }

    private void SubscribeBackgroundUpdates(CancellationToken ct)
    {
        Observable.Timer(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15))
            .Select(_ => Observable.FromAsync(UpdateBalancesAsync)).Concat().Subscribe(ct);
        Observable.Timer(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
            .Select(_ => Observable.FromAsync(UpdateSymbolsAsync)).Concat().Subscribe(ct);

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

                _orders[orderUpdate.ClientOrderId] = orderUpdate;
            }, ct: ct));
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
        _log.LogInformation("Balance Updated. Total Equity: {totalEquity:#.000}", AccountBalance.TotalEquity);
    }

    private async Task UpdateOrdersAsync(CancellationToken ct)
    {
        var activeOrders =
            await ApiCallHelper.ExecuteWithRetry(() => _restClient.UnifiedApi.Trading.GetOrdersAsync(ct: ct), ct: ct);
        if (!activeOrders.GetResultOrError(out var data, out var error))
        {
            _log.LogWarning("Error loading orders: {message}", error);
            return;
        }

        foreach (var order in data)
        {
            if (string.IsNullOrWhiteSpace(order.ClientOrderId)) continue;

            _orders[order.ClientOrderId] = order;
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
            channel!.Writer.TryWrite(next);
        }
    }
}
