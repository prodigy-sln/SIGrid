using OKX.Net.Objects.Account;

namespace SIGrid.App.GridBot;

public class OKXPositionEqualityComparer : EqualityComparer<OKXPosition>
{
    public override bool Equals(OKXPosition? x, OKXPosition? y)
    {
        return (x?.PositionId.Equals(y?.PositionId.GetValueOrDefault())).GetValueOrDefault();
    }

    public override int GetHashCode(OKXPosition obj)
    {
        return obj.PositionId.GetHashCode();
    }
}
