namespace SIGrid.App.GridBot.Extensions;

public static class EnumerableExtensions
{
    public static IEnumerable<IEnumerable<T>> Slice<T>(this IEnumerable<T> enumerable, int sliceSize)
    {
        return enumerable
            .Select((item, idx) => (item, idx))
            .GroupBy(e => (int) e.idx / sliceSize)
            .Select(e => e.Select(x => x.item));
    }

    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> enumerable)
    {
        return enumerable.Where(x => x != null).Select(x => x!);
    }
}
