using OKX.Net.Enums;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;

namespace SIGrid.App.GridBot.Extensions;

public static class OKXOrderUpdateExtensions
{
    public static decimal GetFillFeeNotionalUsd(this OKXOrderUpdate orderUpdate, OKXInstrument instrument)
    {
        switch (instrument.ContractType)
        {
            case OKXContractType.Linear:
                return orderUpdate.FillFee;
            case OKXContractType.Inverse:
                return orderUpdate.FillFee * orderUpdate.FillPrice ?? 0;
            default:
                return 0;
        }
    }

    public static IEnumerable<GridLineInfo> ToGridLineInfos(this IEnumerable<OKXOrder> orders) => 
        orders.Select(o => new GridLineInfo(o.GetGridLineIndex(), o.Price.GetValueOrDefault(), o));

    public static IEnumerable<OKXOrder> GetActiveOrdersOfSide(this IEnumerable<OKXOrder> orders, OKXOrderSide orderSide) =>
        orders.Where(o => o.OrderSide == orderSide
                          && o is { OrderState: OKXOrderState.Live or OKXOrderState.PartiallyFilled }
        );

    //public static TelemetryOrderUpdate ToTelemetryOrderUpdate(this OKXOrderUpdate orderUpdate, SIGridOptions.TradedSymbolOptions tradedSymbol)
    //{
    //    return new TelemetryOrderUpdate
    //    {
    //        Exchange = tradedSymbol.Exchange,
    //        Symbol = orderUpdate.Symbol,
    //        SymbolType = tradedSymbol.SymbolType,
    //        Side = Enum.Parse<TelemetryOrderUpdate.OrderUpdateSide>(orderUpdate.OrderSide.ToString(), true),
    //        State = orderUpdate.OrderState.ToTelemetryState(),
    //        OrderType = orderUpdate.GetExecutionType(),
    //        ExchangeOrderId = orderUpdate.OrderId?.ToString(),
    //        ClientOrderId = orderUpdate.ClientOrderId,
    //        Quantity = orderUpdate.Quantity.GetValueOrDefault(),
    //        QuantityFilled = orderUpdate.QuantityFilled.GetValueOrDefault(),
    //        FillFee = orderUpdate.FillFee,
    //        NotionalUsd = orderUpdate.NotionalUsd,
    //        FillNotionalUsd = orderUpdate.FillNotionalUsd,
    //        Leverage = orderUpdate.Leverage
    //    };
    //}

    //private static TelemetryOrderUpdate.OrderUpdateState ToTelemetryState(this OKXOrderState orderState)
    //{
    //    switch (orderState)
    //    {
    //        case OKXOrderState.Live:
    //            return TelemetryOrderUpdate.OrderUpdateState.Active;
    //        case OKXOrderState.Canceled:
    //            return TelemetryOrderUpdate.OrderUpdateState.Cancelled;
    //        case OKXOrderState.PartiallyFilled:
    //            return TelemetryOrderUpdate.OrderUpdateState.PartiallyFilled;
    //        case OKXOrderState.Filled:
    //            return TelemetryOrderUpdate.OrderUpdateState.Filled;
    //        default:
    //            return TelemetryOrderUpdate.OrderUpdateState.Undefined;
    //    }
    //}

    //private static TelemetryOrderUpdate.OrderExecutionType? GetExecutionType(this OKXOrderUpdate orderUpdate)
    //{
    //    var executionType = orderUpdate.ExecutionType ?? "_";
    //    switch (executionType)
    //    {
    //        case "T":
    //            return TelemetryOrderUpdate.OrderExecutionType.Taker;
    //        case "M":
    //            return TelemetryOrderUpdate.OrderExecutionType.Maker;
    //        default:
    //            return TelemetryOrderUpdate.OrderExecutionType.Unknown;
    //    }
    //}
}
