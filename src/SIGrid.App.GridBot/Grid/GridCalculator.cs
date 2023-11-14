using McNeight;

namespace SIGrid.App.GridBot.Grid;

public static class GridCalculator
{
    private const decimal BasePrice = 0.00000001M;

    public static decimal GetGridPrice(decimal profitPerGrid, int gridLineIndex, decimal basePrice = BasePrice)
    {
        return basePrice * MathM.Pow(1.0M + profitPerGrid, gridLineIndex);
    }

    public static decimal GetGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return Math.Round(MathM.Log10(currentPrice / basePrice) / MathM.Log10(1.0M + profitPerGrid), 2);
    }

    public static int GetPreviousGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), 0, MidpointRounding.ToZero);
    }

    public static IEnumerable<GridLineInfo> GetGridBuyLinesAndPrices(int startingLine, decimal profitPerGrid, int numLines, decimal basePrice = BasePrice)
    {
        var linesGenerated = 0;
        for (var line = startingLine - 1; linesGenerated < numLines; --line)
        {
            var linePrice = GetGridPrice(profitPerGrid, line, basePrice);

            yield return new GridLineInfo(line, linePrice);
            ++linesGenerated;
        }
    }

    public static IEnumerable<GridLineInfo> GetGridSellLinesAndPrices(int startingLine, decimal profitPerGrid, int numLines, decimal basePrice = BasePrice)
    {
        var linesGenerated = 0;
        for (var line = startingLine + 1; linesGenerated < numLines; ++line)
        {
            var linePrice = GetGridPrice(profitPerGrid, line, basePrice);

            yield return new GridLineInfo(line, linePrice);
            ++linesGenerated;
        }
    }
}
