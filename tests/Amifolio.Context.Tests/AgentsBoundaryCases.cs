using System.Security.Cryptography;
using System.Text;
using Amifolio.Context;

internal static class AgentsBoundaryCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("AGENTS boundary rejects non-AGENTS reads without changing source bytes", RejectReads),
        ("AGENTS boundary rejects direct non-AGENTS saves without changing source bytes", RejectSaves),
        ("AGENTS boundary rejects non-AGENTS undo targets without changing source bytes", RejectUndo),
        ("AGENTS boundary rejects missing non-AGENTS targets without creating files", RejectCreation),
        ("AGENTS boundary allows case-insensitive names and preserves encoding backup append and undo", AllowedNames)
    ];

    private static readonly string[] InvalidNames =
    ["notes.txt", "README.md", "smoke-AGENTS.md", "AGENTS.txt", "AGENTS.md.bak", "AGENTS.md.json", ".AGENTS.md", "config.toml", "hooks.json", "SKILL.md"];

    private static Task RejectReads() => InDirectory(async root =>
    {
        var service = new AgentsFileService();
        foreach (var name in InvalidNames)
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, "项目原文\r\n", Encoding.Unicode);
            await RejectedUnchanged(root, path, () => service.ReadAsync(path));
        }
    });

    private static Task RejectSaves() => InDirectory(async root =>
    {
        var service = new AgentsFileService();
        foreach (var name in InvalidNames)
        {
            var path = Path.Combine(root, name);
            const string original = "项目原文\r\n";
            await File.WriteAllTextAsync(path, original, Encoding.Unicode);
            // A persisted/caller-supplied snapshot must not bypass the save boundary.
            var snapshot = new AgentsFileSnapshot(path, original,
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), true);
            await RejectedUnchanged(root, path, () => service.SaveAsync(snapshot, "覆盖原文\r\n"));
        }
    });

    private static Task RejectUndo() => InDirectory(async root =>
    {
        var service = new AgentsFileService();
        var path = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(path, "保存前\r\n", Encoding.Unicode);
        var backup = await service.SaveAsync(await service.ReadAsync(path), "保存后\r\n");
        Check(backup is not null, "Undo fixture did not create a backup.");
        var backupBytes = await File.ReadAllBytesAsync(backup!);
        var saved = await service.ReadAsync(path);
        var renamed = Path.Combine(root, "notes.txt");
        File.Move(path, renamed);
        // Desktop undo restores backup text through SaveAsync. Exercise that same
        // boundary with a retargeted snapshot, without reading the illegal target first.
        var previous = await File.ReadAllTextAsync(backup!);
        await RejectedUnchanged(root, renamed,
            () => service.SaveAsync(saved with { Path = renamed }, previous));
        var retainedBackup = await File.ReadAllBytesAsync(backup!);
        Check(backupBytes.SequenceEqual(retainedBackup), "Rejected undo changed backup bytes.");
    });

    private static Task RejectCreation() => InDirectory(async root =>
    {
        var service = new AgentsFileService();
        foreach (var name in InvalidNames)
        {
            var path = Path.Combine(root, name);
            await RejectedUnchanged(root, path, () => service.ReadAsync(path));
            var missing = new AgentsFileSnapshot(path, "", Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>())), false);
            await RejectedUnchanged(root, path, () => service.SaveAsync(missing, "不应创建"));
        }
    });

    private static Task AllowedNames() => InDirectory(async root =>
    {
        var service = new AgentsFileService();
        foreach (var name in new[] { "AGENTS.md", "agents.md", "aGeNtS.Md" })
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            const string original = "# 用户规则\r\n保留原有中文和换行。\r\n";
            await File.WriteAllTextAsync(path, original, Encoding.Unicode);
            var originalBytes = await File.ReadAllBytesAsync(path);
            var snapshot = await service.ReadAsync(path);
            var proposed = service.PreviewManagedBlock(snapshot, Path.Combine(root, "context"), true);
            Check(proposed.StartsWith(original, StringComparison.Ordinal), "Append changed original user text.");
            var backup = await service.SaveAsync(snapshot, proposed);
            var backupBytes = backup is null ? Array.Empty<byte>() : await File.ReadAllBytesAsync(backup);
            Check(backup is not null && originalBytes.SequenceEqual(backupBytes), "Backup did not preserve original bytes.");
            var savedBytes = await File.ReadAllBytesAsync(path);
            Check(savedBytes.Length > 2 && savedBytes[0] == 0xff && savedBytes[1] == 0xfe, "Save lost UTF16 BOM.");
            var saved = await service.ReadAsync(path);
            Check(saved.Content == proposed, "Case-insensitive AGENTS name did not save the appended content.");
            Check(await service.SaveAsync(saved, proposed) is null, "Unchanged save created another backup.");
            await service.SaveAsync(saved, await File.ReadAllTextAsync(backup!));
            var restoredBytes = await File.ReadAllBytesAsync(path);
            Check(originalBytes.SequenceEqual(restoredBytes), "Allowed undo failed to restore original bytes and encoding.");
        }
    });

    private static async Task RejectedUnchanged(string root, string path, Func<Task> action)
    {
        var existed = File.Exists(path);
        var before = existed ? await File.ReadAllBytesAsync(path) : [];
        var entries = Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal).ToArray();
        var rejected = false;
        try { await action(); }
        catch (Exception e) when (e is ArgumentException or IOException) { rejected = true; }
        var existsNow = File.Exists(path);
        var after = existsNow ? await File.ReadAllBytesAsync(path) : Array.Empty<byte>();
        Check(existsNow == existed && (!existed || before.SequenceEqual(after)),
            "Non-AGENTS operation changed source bytes: " + Path.GetFileName(path));
        Check(entries.SequenceEqual(Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal)),
            "Non-AGENTS operation created or removed files: " + Path.GetFileName(path));
        Check(rejected, "Expected non-AGENTS target rejection: " + Path.GetFileName(path));
    }

    private static async Task InDirectory(Func<string, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "amifolio-agents-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await run(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
