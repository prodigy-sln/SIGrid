namespace SIGrid.App.GridBot;

public class GridState
{
    public GridState(GridLineInfo[] buyOrderLines, GridLineInfo[] sellOrderLines)
    {
        BuyOrderLines = buyOrderLines;
        SellOrderLines = sellOrderLines;
    }

    public GridLineInfo[] BuyOrderLines { get; init; }

    public GridLineInfo[] SellOrderLines { get; init; }

    public override string ToString() => 
        $"BUY: [{string.Join(", ", BuyOrderLines.Select(l => l.Line).Order())}]; SELL: [{string.Join(", ", SellOrderLines.Select(l => l.Line).Order())}]";
}
