namespace SIGrid.App.GridBot.Grid;

public class GridLineInfoEqualityComparer : IEqualityComparer<GridLineInfo>
{
    public static GridLineInfoEqualityComparer Instance = new();

    public bool Equals(GridLineInfo x, GridLineInfo y)
    {
        return x.Line == y.Line && x.Price == y.Price && x.Quantity == y.Quantity;
    }

    public int GetHashCode(GridLineInfo obj)
    {
        return HashCode.Combine(obj.Line, obj.Price, obj.Quantity);
    }
}