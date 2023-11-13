using InfluxDB.Client.Core;
using SIGrid.Exchange.Shared;

namespace SIGrid.Telemetry;

[Measurement("price")]
public class TelemetrySymbolPriceUpdate
{
    public TelemetrySymbolPriceUpdate(SIGridOptions.TradedSymbolOptions tradedSymbol, decimal price)
    {
        Exchange = tradedSymbol.Exchange;
        Symbol = tradedSymbol.Symbol;
        SymbolType = tradedSymbol.SymbolType;
        Price = price;
        Time = DateTime.UtcNow;
    }

    [Column("e", IsTag = true)]
    public string Exchange { get; init; }

    [Column("s", IsTag = true)]
    public string Symbol { get; init; }

    [Column("st", IsTag = true)]
    public string SymbolType { get; init; }

    [Column("price")]
    public decimal Price { get; init; }

    [Column(IsTimestamp = true)]
    public DateTime Time { get; init; }
}
