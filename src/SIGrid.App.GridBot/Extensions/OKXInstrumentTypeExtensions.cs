using OKX.Net.Enums;

namespace SIGrid.App.GridBot.Extensions;

public static class OKXInstrumentTypeExtensions
{
    public static OKXInstrumentType ToOKXInstrumentTypeOrException(this string value)
    {
        if (Enum.TryParse<OKXInstrumentType>(value, true, out var instrumentType))
        {
            return instrumentType;
        }

        throw new ArgumentException(nameof(value), $"Invalid instrument type {value} for OKX.");
    }
}
