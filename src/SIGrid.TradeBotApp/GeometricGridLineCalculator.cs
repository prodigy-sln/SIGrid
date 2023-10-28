namespace SIGrid.TradeBotApp;

public class GeometricGridLineCalculator
{
    public decimal GetNextLineFromPrice(decimal priceFrom, decimal spacing)
    {
        var multiplier = (100m + spacing) / 100m;
        return priceFrom * multiplier;
    }

    public IEnumerable<decimal> GetLinesFromPrice(decimal priceFrom, decimal spacing, int numLines)
    {
        decimal currentPrice = priceFrom;
        for (int i = 0; i < numLines; i++)
        {
            currentPrice = GetNextLineFromPrice(currentPrice, spacing);
            yield return currentPrice;
        }
    }
}
