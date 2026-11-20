using Amifolio.Core;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Stage9Upgrade
{
    internal static async Task<int> Run(string root,string destination,string report)
    {
        root=ArchiveSafety.LocalPath(root);destination=ArchiveSafety.LocalPath(destination);report=ArchiveSafety.LocalPath(report);
        ValidateOutputs(root,destination,report);
        if(!File.Exists(Path.Combine(root,"portfolio.db"))||File.Exists(destination)||File.Exists(report))throw new IOException("Upgrade requires an existing library and fresh output files.");
        if(destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||report.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Recovery outputs must be outside the library.");
        var key=Path.Combine(root,"settings","ai.json");var learning=Path.Combine(root,"learning","state.sqlite");var cache=Path.Combine(root,"browser-index.json");
        static string? Digest(string path)=>File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):null;
        var savedKey=Digest(key);var savedLearning=Digest(learning);var savedCache=Digest(cache);
        await new PortfolioMaintenance(root).BackupAsync(destination);
        // The encrypted local credential blob is a separate opaque rollback file, never a portable ZIP entry.
        if(savedKey is not null)File.Copy(key,destination+".local-key",false);
        if(savedKey!=Digest(key)||savedLearning!=Digest(learning)||savedCache!=Digest(cache))throw new IOException("Current data changed during backup; migration has not started.");
        var migration=await new LegacyDataService(root).MigrateAsync();
        if(savedKey!=Digest(key)||savedLearning!=Digest(learning)||savedCache!=Digest(cache))throw new IOException("Current data integrity check failed.");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{status=migration.Status,directoriesMoved=migration.DirectoriesMoved,historyDirectory=migration.HistoryDirectory,backup=destination,keyPreserved=true,learningPreserved=true,browserCachePreserved=true,projectOriginalsAccessed=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("stage9-upgrade-preserved-current-data");return 0;
    }
    internal static void ValidateOutputs(string root,string destination,string report)
    {
        var outputs=new[]{destination,destination+".local-key",report}.Select(ArchiveSafety.LocalPath).ToArray();
        if(outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=3)throw new IOException("Upgrade outputs must be distinct.");
        foreach(var path in outputs)
        {
            ArchiveSafety.NoReparse(path);
            if(ArchiveSafety.Within(path,root)||File.Exists(path)||Directory.Exists(path))throw new IOException("Upgrade requires fresh outputs outside the library.");
        }
        ArchiveSafety.NoReparse(root);
    }
}
