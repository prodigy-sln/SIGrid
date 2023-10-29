﻿using McNeight;

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
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), 0, MidpointRounding.ToEven);
    }

    public static int GetNextGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), 0, MidpointRounding.ToPositiveInfinity);
    }

    public static int GetPreviousGridLineIndex(decimal currentPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return (int)Math.Round(GetGridLineIndex(currentPrice, profitPerGrid, basePrice), 0, MidpointRounding.ToZero);
    }

    public static IEnumerable<(int Index, decimal Price)> GetGridBuyLinesAndPrices(decimal currentPrice, decimal profitPerGrid, int numLines, decimal basePrice = BasePrice)
    {
        var previousGridLine = GetNextGridLineIndex(currentPrice, profitPerGrid, basePrice) - 2;
        var linesGenerated = 0;
        for(var line = previousGridLine; linesGenerated < numLines; --line)
        {
            var linePrice = GetGridPrice(profitPerGrid, line, basePrice);

            if (linePrice >= currentPrice) continue;

            yield return (line, linePrice);
            ++linesGenerated;
        }
    }

    public static int GetSellGridLineFromBuyPrice(decimal buyPrice, decimal profitPerGrid, decimal basePrice = BasePrice)
    {
        return GetNextGridLineIndex(buyPrice, profitPerGrid, basePrice);
        //var buyLine = GetGridBuyLinesAndPrices(buyPrice, profitPerGrid, 1, basePrice).FirstOrDefault();
        //var buyLineIdx = buyLine.Index + 2;
        //return buyLineIdx;
    }
}
