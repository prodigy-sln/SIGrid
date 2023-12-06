using OKX.Net.Enums;
using OKX.Net.Objects.Trade;
using SIGrid.App.GridBot.Grid;

namespace SIGrid.App.GridBot.Extensions;

public static class OKXOrderUpdateExtensions
{
    public static IEnumerable<GridLineInfo> ToGridLineInfos(this IEnumerable<OKXOrder> orders) => 
        orders.Select(o => new GridLineInfo(o.GetGridLineIndex(), o.Price.GetValueOrDefault(), o.Quantity.GetValueOrDefault(), o));

    public static IEnumerable<OKXOrder> GetActiveOrdersOfSide(this IEnumerable<OKXOrder> orders, OKXOrderSide orderSide) =>
        orders.Where(o => o.OrderSide == orderSide
                          && o is { OrderState: OKXOrderState.Live or OKXOrderState.PartiallyFilled }
        );
}
