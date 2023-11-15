using OKX.Net.Enums;
using OKX.Net.Objects.Public;
using OKX.Net.Objects.Trade;
using SIGrid.App.GridBot.Grid;

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
}
