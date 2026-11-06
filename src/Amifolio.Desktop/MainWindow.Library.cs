using System.Diagnostics;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task RefreshProjectsAsync()
    {
        projects = await Task.Run(() => store.ProjectsAsync());
        updating = true;
        try
        {
            ProjectsList.ItemsSource = projects;
            SetScopes(ProjectFilter, "全部项目");
            SetScopes(EditProject, "全局 · 未归属项目");
            SetScopes(ContextScope, "全局");
            SetScopes(AgentsScope, "全局 Codex");
        }
        finally { updating = false; }
    }

    private void SetScopes(ComboBox box, string globalLabel)
    {
        var previous = SelectedScope(box)?.Id;
        var choices = new[] { new ScopeChoice(null, globalLabel) }.Concat(projects.Select(x => new ScopeChoice(x.Id, x.Name))).ToList();
        box.ItemsSource = choices;
        box.SelectedItem = choices.FirstOrDefault(x => x.Id == previous) ?? choices[0];
    }

    private async Task RefreshLibraryAsync()
    {
        var search = SearchBox.Text;
        var projectId = SelectedScope(ProjectFilter)?.Id;
        var kind = KindFilter.SelectedItem as string;
        var source = SourceFilter.SelectedItem as string;
        var records = await Task.Run(() => store.ListAsync(search, projectId, kind == "全部类型" ? null : kind));
        if (source is not null and not "全部来源") records = records.Where(x => SourceCategory(x.Source) == source).ToList();
        var items = records.Select(x => new ArtifactItem(x, x.Title,
            $"{x.Source}  ·  {projects.FirstOrDefault(p => p.Id == x.ProjectId)?.Name ?? "全局"}  ·  v{x.Version}  {x.Tags}",
            BadgeFor(x), FormatBytes(x.Bytes))).ToList();
        ArtifactList.ItemsSource = items;
        ArtifactList.SelectedItem = items.FirstOrDefault(x => x.Record.Id == selectedArtifactId);
        EmptyLibrary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = items.Count == 0 && (!string.IsNullOrWhiteSpace(search) || projectId is not null || kind is not null and not "全部类型" || source is not null and not "全部来源") ? "无匹配作品" : "暂无作品";
        LibraryCount.Text = $"{items.Count} 件作品";
        if (kind is null or "全部类型")
        {
            updating = true;
            KindFilter.ItemsSource = new[] { "全部类型" }.Concat(records.Select(x => x.Kind).Distinct().Order()).ToList();
            KindFilter.SelectedIndex = 0;
            updating = false;
        }
        if (ArtifactList.SelectedItem is null) ClearDetail();
    }

    private static string BadgeFor(ArtifactRecord item)
    {
        if (item.Kind.Contains("代码", StringComparison.OrdinalIgnoreCase) || item.Kind.Contains("Repository", StringComparison.OrdinalIgnoreCase)) return "CODE";
        var extension = Path.GetExtension(item.OriginalName).TrimStart('.').ToUpperInvariant();
        return extension.Length is > 0 and <= 5 ? extension : "FILE";
    }

    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || updating) return;
        searchDelay?.Cancel();
        var delay = new CancellationTokenSource();
        searchDelay = delay;
        try { await Task.Delay(220, delay.Token); if (operation is null) await RefreshLibraryAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "检索失败：" + ex.Message; }
        finally { if (searchDelay == delay) searchDelay = null; delay.Dispose(); }
    }

    private async void FiltersChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || operation is not null) return;
        await RunAsync("正在筛选作品", async _ => { await RefreshLibraryAsync(); StatusText.Text = "筛选已更新"; });
    }

    private void ArtifactSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ArtifactList.SelectedItem is not ArtifactItem item) { ClearDetail(); return; }
        var artifact = item.Record;
        selectedArtifactId = artifact.Id;
        DetailTitle.Text = artifact.Title;
        DetailMetadata.Text = $"{artifact.Kind}\n\n来源  {artifact.Source}\n项目  {projects.FirstOrDefault(p => p.Id == artifact.ProjectId)?.Name ?? "全局"}\n版本  {artifact.Version} · {FormatBytes(artifact.Bytes)}\n保存  {artifact.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}\n提取  {artifact.ExtractionStatus}\nSHA256  {artifact.Hash[..Math.Min(12, artifact.Hash.Length)]}…";
        DetailPreview.Text = string.IsNullOrWhiteSpace(artifact.Preview) ? "暂无预览" : artifact.Preview[..Math.Min(1800, artifact.Preview.Length)];
        DetailPreviewHeading.Visibility = DetailDivider.Visibility = Visibility.Visible;
        EditTitle.Text = artifact.Title;
        EditTags.Text = artifact.Tags;
        EditProject.SelectedItem = EditProject.Items.OfType<ScopeChoice>().FirstOrDefault(x => x.Id == artifact.ProjectId);
        DetailActions.Visibility = Visibility.Visible;
        if (!loadingPage) motion?.Enter(DetailContent, MotionKind.Detail);
    }

    private void ClearDetail()
    {
        selectedArtifactId = null;
        DetailTitle.Text = "未选择作品";
        DetailMetadata.Text = "";
        DetailPreview.Text = "";
        DetailPreviewHeading.Visibility = DetailDivider.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = Visibility.Collapsed;
    }

    private async void ImportFiles(object sender, RoutedEventArgs e)
    {
        if (!ready || operation is not null) return;
        await RunAsync("选择文件", async token =>
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) { StatusText.Text = "未选择文件"; return; }
            var projectId = SelectedScope(ProjectFilter)?.Id;
            var imported = 0;
            var failures = new List<string>();
            var warnings = new List<string>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                StatusText.Text = $"正在保存 {++imported}/{files.Count} · {Path.GetFileName(file.Path)}";
                try
                {
                    var result = await Task.Run(() => store.ImportFileAsync(file.Path, projectId, cancellationToken: token), token);
                    warnings.AddRange(result.Warnings);
                    selectedArtifactId = result.Artifact.Id;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(file.Path)}：{ex.Message}"); }
            }
            await RefreshLibraryAsync();
            StatusText.Text = $"导入完成：{files.Count - failures.Count} 个成功，{failures.Count} 个失败";
            if (failures.Count > 0 || warnings.Count > 0)
                await ShowMessageAsync("导入结果", string.Join("\n", failures.Concat(warnings).Take(12)));
        });
    }

    private async void ImportRepository(object sender, RoutedEventArgs e)
    {
        if (!ready || operation is not null) return;
        await RunAsync("选择代码库目录", async token =>
        {
            var picker = new FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { StatusText.Text = "未选择目录"; return; }
            if (!await ConfirmAsync("保存源码快照", "将复制源码并排除 .git、依赖、构建目录及常见密钥文件；不会执行代码。这是源码快照，不能替代完整 Git 备份。", "保存快照")) return;
            var projectId = SelectedScope(ProjectFilter)?.Id;
            var result = await Task.Run(() => store.ImportRepositoryAsync(folder.Path, projectId, cancellationToken: token), token);
            selectedArtifactId = result.Artifact.Id;
            await RefreshLibraryAsync();
            StatusText.Text = "代码库快照已保存";
            if (result.Warnings.Count > 0) await ShowMessageAsync("快照排除与提示", string.Join("\n", result.Warnings.Take(12)));
        });
    }

    private async void SaveArtifact(object sender, RoutedEventArgs e)
    {
        if (selectedArtifactId is not { } id) return;
        var title = EditTitle.Text.Trim();
        var tags = EditTags.Text;
        var projectId = SelectedScope(EditProject)?.Id;
        await RunAsync("正在保存分类", async token =>
        {
            await Task.Run(() => store.UpdateArtifactAsync(id, title, tags, projectId, token), token);
            await RefreshLibraryAsync();
            StatusText.Text = "名称与分类已保存";
        });
    }

    private async void RevealArtifact(object sender, RoutedEventArgs e)
    {
        if (selectedArtifactId is not { } id) return;
        await RunAsync("定位作品副本", async token =>
        {
            var path = await store.GetOriginalPathAsync(id, token);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
            StatusText.Text = "已显示文件副本";
        });
    }

    private async void RemoveArtifact(object sender, RoutedEventArgs e)
    {
        if (selectedArtifactId is not { } id) return;
        await RunAsync("移除作品", async token =>
        {
            if (!await ConfirmAsync("从作品集移除？", "将移除该作品记录。原始来源文件保持不变；已导出的上下文需另行检查、删除或重新发布。", "移除")) return;
            await store.RemoveArtifactAsync(id, token);
            selectedArtifactId = null;
            await RefreshLibraryAsync();
            StatusText.Text = "作品已移除，原始来源文件未更改";
        });
    }

    private async void CreateProject(object sender, RoutedEventArgs e)
    {
        await RunAsync("新建项目", async token =>
        {
            var name = new TextBox { Header = "项目名称", PlaceholderText = "例如：桌面工具" };
            var rootPath = new TextBox { Header = "本机目录（可选）", PlaceholderText = "用于定位项目的 AGENTS.md" };
            var choose = new Button { Content = "选择目录" };
            choose.Click += async (_, _) =>
            {
                var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                if (folder is not null) rootPath.Text = folder.Path;
            };
            var panel = new StackPanel { Spacing = 16, MinWidth = 380 };
            panel.Children.Add(name); panel.Children.Add(rootPath); panel.Children.Add(choose);
            if (!await ConfirmAsync("新建项目", panel, "创建")) return;
            var project = await store.CreateProjectAsync(name.Text.Trim(), string.IsNullOrWhiteSpace(rootPath.Text) ? null : rootPath.Text.Trim(), token);
            await RefreshProjectsAsync();
            StatusText.Text = $"项目“{project.Name}”已创建";
        });
    }
}
