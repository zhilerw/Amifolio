using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunNativeMemorySmokeAsync(string reportPath)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native memory regression: " + name);
            checks.Add(name);
        }

        await NavigateAsync("native-memory");
        Check(NativeMemoryPage.Visibility == Visibility.Visible && NativeMemoryFiles.Items.Count == 0 &&
            NativeMemoryStatus.Text.Contains("暂无记录"), "empty profile layout");

        var project = await SetupCatalogSmokeAsync();
        var v1 = Path.Combine(integration.CodexHome, "memories");
        var v2 = Path.Combine(integration.CodexHome, "memories_v2");
        var summaries = Path.Combine(v2, "rollout_summaries");
        var notes = Path.Combine(v2, "extensions", "ad_hoc", "notes");
        foreach (var folder in new[] { v1, summaries, notes }) Directory.CreateDirectory(folder);
        var overview = Path.Combine(v2, "memory_summary.md");
        var pending = Path.Combine(notes, "2026-11-23-m4-fixture.md");
        var source = Path.Combine(summaries, "m4-fixture.md");
        await File.WriteAllTextAsync(Path.Combine(v1, "MEMORY.md"), "# v1 · 旧版测试记忆");
        await File.WriteAllTextAsync(overview, "v1\n## User Preferences\n#24766D");
        await File.WriteAllTextAsync(pending, "测试更正：标题使用宋体。");

        static Task SummaryAsync(string path, string projectRoot, string sourceDirectory) =>
            File.WriteAllTextAsync(path, "thread_id: 019a0000-0000-7000-8000-000000000001\n" +
                "updated_at: 2026-11-23T02:00:00Z\nrollout_path: " + Path.Combine(sourceDirectory, "fixture.jsonl") +
                "\ncwd: " + projectRoot + "\n\n# 来源摘要\n单独保存了样式偏好。");

        await SummaryAsync(source, project.RootPath, summaries);
        await SummaryAsync(Path.Combine(summaries, "other.md"), project.RootPath + "-other", summaries);
        await NavigateAsync("native-memory");
        Check(NativeMemoryStatus.Text.Contains("v1 有本机记录") && NativeMemoryStatus.Text.Contains("v2 有本机记录") &&
            NativeMemoryStatus.Text.Contains("宿主会话"), "coexisting stores and explicit host capability");

        var items = NativeMemoryFiles.Items.OfType<NativeMemoryChoice>().ToArray();
        NativeMemoryFiles.SelectedItem = items.Single(item => item.Source.Path == overview);
        await Task.Yield();
        Check(NativeMemoryText.IsReadOnly && NativeMemoryText.Text.Contains("#24766D") &&
            NativeMemoryFileInfo.Text.Contains(overview) && NativeMemoryFileInfo.Text.Contains("文件最近修改"), "overview renders read-only with path and timestamp");

        NativeMemoryFiles.SelectedItem = items.Single(item => item.Source.Path == pending);
        await Task.Yield();
        Check(NativeMemoryText.Text.Contains("标题使用宋体") && NativeMemoryFileInfo.Text.Contains("是否整理进摘要"), "pending note remains a note");

        await File.AppendAllTextAsync(overview, "\n更新：#3D719C");
        await ShowNativeMemorySnapshotAsync(backgroundTasks.Token);
        Check(NativeMemoryStatus.Text.Contains("1 项文件变化") &&
            (NativeMemoryFiles.SelectedItem as NativeMemoryChoice)?.Source.Path == pending, "refresh detects changes and preserves selection");
        NativeMemoryFiles.SelectedItem = NativeMemoryFiles.Items.OfType<NativeMemoryChoice>().Single(item => item.Source.Path == overview);
        await Task.Yield();
        Check(NativeMemoryText.Text.Contains("#3D719C"), "refresh surfaces updated text");

        NativeMemoryScope.SelectedItem = NativeMemoryScope.Items.OfType<ScopeChoice>().Single(item => item.Id == project.Id);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (operation is not null) await Task.Delay(20, timeout.Token);
        Check(NativeMemoryFiles.Items.Count == 1 && ((NativeMemoryChoice)NativeMemoryFiles.Items[0]).Source.Path == source &&
            NativeMemoryStatus.Text.Contains("当前项目一致"), "project filter keeps only verifiable sources");
        await RenderSnapshotAsync(reportPath + ".native-memory.png");

        await File.AppendAllTextAsync(overview, "\n- rollout_summaries/m4-fixture.md");
        File.Delete(source);
        await ShowNativeMemorySnapshotAsync(backgroundTasks.Token);
        Check(NativeMemoryFiles.Items.Count == 0 && NativeMemoryStatus.Text.Contains("1 项文件变化"),
            "refresh reports selected project source removal");
        return new { checks };
    }
}
