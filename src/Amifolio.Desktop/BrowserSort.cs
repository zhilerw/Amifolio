namespace Amifolio.Desktop;

internal enum BrowserSortField { Name, Type, Modified, Size }
internal readonly record struct BrowserSortKey(bool IsDirectory, string Name, string Type, DateTimeOffset Modified, long Bytes);
internal static class BrowserSort
{
    public static IReadOnlyList<T> Apply<T>(IEnumerable<T> items, Func<T, BrowserSortKey> key, BrowserSortField field, bool descending)
    {
        var foldersFirst = items.OrderByDescending(x => key(x).IsDirectory);
        // LINQ ordering is stable; reversing the sequence would reverse equal keys and folders.
        return field switch
        {
            BrowserSortField.Type => Order(x => key(x).Type, StringComparer.OrdinalIgnoreCase),
            BrowserSortField.Modified => Order(x => key(x).Modified, Comparer<DateTimeOffset>.Default),
            BrowserSortField.Size => Order(x => key(x).Bytes, Comparer<long>.Default),
            _ => Order(x => key(x).Name, StringComparer.OrdinalIgnoreCase)
        };
        T[] Order<K>(Func<T, K> selector, IComparer<K> comparer) =>
            (descending ? foldersFirst.ThenByDescending(selector, comparer) : foldersFirst.ThenBy(selector, comparer)).ToArray();
    }
}
