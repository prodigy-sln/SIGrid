using OKX.Net.Enums;

namespace SIGrid.TradeBot;

public class OKXTradedSymbol
{
    public OKXInstrumentType InstrumentType { get; set; }

    public string Symbol { get; set; }

    public decimal InvestPerGrid { get; set; } = 10.0M;

    public decimal TakeProfitPercent { get; set; } = 1.0M;

    public decimal ReinvestProfit { get; set; } = 0.0M;

    public decimal Leverage { get; set; } = 0.0M;

    public int MaxActiveBuyOrders { get; set; } = 10;

    public int MaxActiveSellOrders { get; set; } = 10;
}