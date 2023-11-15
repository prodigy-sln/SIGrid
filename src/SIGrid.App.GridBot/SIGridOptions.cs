using System.ComponentModel.DataAnnotations;

namespace SIGrid.App.GridBot;

public class SIGridOptions
{
    public List<TradedSymbolOptions> TradedSymbols { get; set; } = new();

    public class TradedSymbolOptions
    {
        [Required]
        public string Exchange { get; set; } = null!;
        
        [Required]
        public string SymbolType { get; set; } = null!;
        
        [Required]
        public string Symbol { get; set; } = null!;

        public InvestCurrencyType InvestCurrency { get; set; }

        public decimal InvestPerGrid { get; set; } = 10.0M;

        public decimal TakeProfitPercent { get; set; } = 1.0M;

        public decimal ReinvestProfit { get; set; } = 0.0M;

        public decimal Leverage { get; set; } = 1.0M;

        public int MaxActiveBuyOrders { get; set; } = 10;

        public int MaxActiveSellOrders { get; set; } = 10;

        public enum InvestCurrencyType
        {
            Base,
            Quote
        }
    }
}
