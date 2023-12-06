using OKX.Net.Objects.Trade;

namespace SIGrid.App.GridBot.Grid;

public struct GridLineInfo
{
    public GridLineInfo(int line, decimal price, decimal quantity, OKXOrder? order = null)
    {
        Line = line;
        Price = price;
        Order = order;
        Quantity = quantity;
    }

    public int Line { get; init; }

    public decimal Price { get; init; }

    public decimal Quantity { get; init; }

    public OKXOrder? Order { get; set; }
}
