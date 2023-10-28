using OKX.Net.Enums;

namespace SIGrid.TradeBot;

public struct GridLineOrderId : IComparable<GridLineOrderId>, IEquatable<GridLineOrderId>
{
    public Guid Id { get; set; }

    public int LineIndex => BitConverter.ToInt32(new ReadOnlySpan<byte>(ByteArray, 0, 4));

    public OKXOrderSide OrderSide => (OKXOrderSide)BitConverter.ToInt16(new ReadOnlySpan<byte>(ByteArray, 4, 2));

    public short SellCount => BitConverter.ToInt16(new ReadOnlySpan<byte>(ByteArray, 6, 2));

    private byte[]? _byteArray;

    private byte[] ByteArray => _byteArray ??= Id.ToByteArray();

    public static GridLineOrderId Create(int index, OKXOrderSide orderSide, short sellCount = 0) =>
        new()
        {
            Id = new Guid(index, (short)orderSide, sellCount, BitConverter.GetBytes(DateTime.UtcNow.Ticks))
        };

    public static bool TryParse(string? input, out GridLineOrderId gridLineOrderId)
    {
        if (!string.IsNullOrWhiteSpace(input) && Guid.TryParse(input, out Guid guid))
        {
            gridLineOrderId = guid;
            return true;
        }

        gridLineOrderId = Guid.Empty;
        return false;
    }

    public static implicit operator GridLineOrderId(Guid id) => new() { Id = id };

    public static implicit operator string(GridLineOrderId gridLineOrderId) => gridLineOrderId.ToString();

    public static implicit operator GridLineOrderId(string gridLineOrderId) => Guid.TryParse(gridLineOrderId, out var guid) ? guid : Guid.Empty;

    public override string ToString() => Id.ToString("N");

    public int CompareTo(GridLineOrderId other)
    {
        var lineIndexComparison = LineIndex.CompareTo(other.LineIndex);
        if (lineIndexComparison != 0) return lineIndexComparison;
        var orderSideComparison = OrderSide.CompareTo(other.OrderSide);
        if (orderSideComparison != 0) return orderSideComparison;
        return SellCount.CompareTo(other.SellCount);
    }

    public bool Equals(GridLineOrderId other) => LineIndex == other.LineIndex && OrderSide == other.OrderSide && SellCount == other.SellCount;

    public override bool Equals(object? obj) => obj is GridLineOrderId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(LineIndex, (int)OrderSide, SellCount);
}
