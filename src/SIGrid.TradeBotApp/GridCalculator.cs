using McNeight;

namespace SIGrid.TradeBotApp;

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

    public static int GetNearestGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), MidpointRounding.ToEven);
    }

    public static int GetNextGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), MidpointRounding.ToPositiveInfinity);
    }

    public static int GetPreviousGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), MidpointRounding.ToZero);
    }

    public static IEnumerable<(int Index, decimal Price)> GetGridLinePrices(decimal currentPrice, decimal profitPerGrid, int numLines, decimal basePrice = BasePrice)
    {
        int lastLine = GetPreviousGridLineIndex(currentPrice, profitPerGrid, basePrice) - 1;
        foreach (int line in Enumerable.Range(lastLine - numLines + 1, numLines))
        {
            yield return (line, GetGridPrice(profitPerGrid, line, basePrice));
        }
    }
}
