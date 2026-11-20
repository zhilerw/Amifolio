namespace Amifolio.Core;

// Internal deterministic fault seam. No environment variable, command or public API enables it.
internal static class StorageFaults
{
    internal static readonly AsyncLocal<Action<string>?> Handler=new();
}
