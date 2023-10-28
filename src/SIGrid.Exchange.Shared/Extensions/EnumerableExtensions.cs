namespace SIGrid.Exchange.Shared.Extensions;

public static class EnumerableExtensions
{
    public static async Task<T[]> ToArrayOrEmptyAsync<T>(this Task<IEnumerable<T>?> task)
    {
        var result = await task;
        return result?.ToArray() ?? Array.Empty<T>();
    }
}