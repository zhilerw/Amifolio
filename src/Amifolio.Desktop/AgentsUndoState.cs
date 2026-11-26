namespace Amifolio.Desktop;

internal sealed class AgentsUndoState
{
    private string? targetPath;
    private string? savedHash;
    public string? BackupPath { get; private set; }
    public bool HasBackup => BackupPath is not null;
    public void Record(string path, string? backup, string hash)
    {
        targetPath = Path.GetFullPath(path);
        BackupPath = backup;
        savedHash = hash;
    }
    public void FileLoaded(string path, string hash, bool exists)
    {
        if (!CanRestore(path, hash, exists)) Clear();
    }
    public bool CanRestore(string path, string hash, bool exists) =>
        exists && HasBackup && SameTarget(path) && savedHash == hash;
    private bool SameTarget(string path) => string.Equals(targetPath, Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public void Clear() { targetPath = savedHash = BackupPath = null; }
}
