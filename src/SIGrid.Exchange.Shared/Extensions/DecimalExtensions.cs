namespace SIGrid.Exchange.Shared.Extensions;

public static class DecimalExtensions
{
    private const int SignMask = ~int.MinValue;

    public static int GetDecimalPointCount(this decimal value)
    {
        return value.Scale;
        //return (decimal.GetBits(value)[3] & SignMask) >> 16;
    }
}
