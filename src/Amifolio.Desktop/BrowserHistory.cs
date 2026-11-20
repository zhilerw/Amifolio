namespace Amifolio.Desktop;

/// <summary>In-memory, bounded history. Validation is supplied by the catalog at use time.</summary>
internal sealed class BrowserHistory<T>(T initial, int capacity = 64, IEqualityComparer<T>? comparer = null)
{
    private readonly List<T> entries = [initial];
    private readonly int limit = capacity >= 2 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly IEqualityComparer<T> equality = comparer ?? EqualityComparer<T>.Default;
    private int index;
    public T Current => entries[index];
    public int Count => entries.Count;
    public bool CanGoBack => index > 0;
    public bool CanGoForward => index + 1 < entries.Count;
    public void Visit(T location)
    {
        if (equality.Equals(Current, location)) return;
        entries.RemoveRange(index + 1, entries.Count - index - 1);
        entries.Add(location);
        if (entries.Count > limit) entries.RemoveAt(0);
        index = entries.Count - 1;
    }
    public void ReplaceCurrent(T location)
    {
        entries[index] = location;
        entries.RemoveRange(index + 1, entries.Count - index - 1);
        if (index > 0 && equality.Equals(entries[index - 1], location)) entries.RemoveAt(index--);
    }
    public async Task<bool> MoveAsync(int direction, Func<T, Task<bool>> isValid)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var next = index + direction;
        while (next >= 0 && next < entries.Count)
        {
            if (await isValid(entries[next])) { index = next; return true; }
            entries.RemoveAt(next);
            if (next < index) index--;
            next = index + direction;
        }
        return false;
    }
}
