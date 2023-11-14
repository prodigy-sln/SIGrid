using OKX.Net.Objects.Trade;

namespace SIGrid.App.GridBot.Grid;

public struct GridLineInfo
{
    public GridLineInfo(int line, decimal price, OKXOrder? order = null)
    {
        Line = line;
        Price = price;
        Order = order;
    }

    public int Line { get; init; }

    public decimal Price { get; init; }

    public OKXOrder? Order { get; init; }
}
