using InfluxDB.Client.Core;

namespace SIGrid.Telemetry;

[Measurement("order")]
public class TelemetryOrderUpdate
{
    [Column("e", IsTag = true)]
    public string Exchange { get; init; }

    [Column("s", IsTag = true)]
    public string Symbol { get; init; }

    [Column("st", IsTag = true)]
    public string SymbolType { get; init; }

    [Column("side", IsTag = true)]
    public OrderUpdateSide Side { get; init; }

    [Column("state", IsTag = true)]
    public OrderUpdateState State { get; init; }

    [Column("ot", IsTag = true)]
    public OrderExecutionType? OrderType { get; init; }

    [Column("oid")]
    public string? ExchangeOrderId { get; init; }

    [Column("cid")]
    public string? ClientOrderId { get; init; }

    [Column("q")]
    public decimal Quantity { get; init; }

    [Column("qf")]
    public decimal QuantityFilled { get; init; }

    [Column("fee")]
    public decimal? FillFee { get; init; }

    [Column("nusd")]
    public decimal? NotionalUsd { get; init; }

    [Column("nusdf")]
    public decimal? FillNotionalUsd { get; init; }

    [Column("lvg")]
    public decimal? Leverage { get; init; }

    [Column(IsTimestamp = true)]
    public DateTime Time { get; init; }

    public enum OrderExecutionType
    {
        Unknown,
        Maker,
        Taker
    }

    public enum OrderUpdateSide
    {
        Undefined,
        Buy,
        Sell
    }

    public enum OrderUpdateState
    {
        Undefined,
        Active,
        Cancelled,
        PartiallyFilled,
        Filled
    }
}
