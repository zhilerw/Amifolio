using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    // Explicit developer switch: inspect native page layout using an isolated data root.
    // This does not claim to replace physical input or a fresh Codex host integration test.
    private async Task RunNativeSmokeAsync(string reportPath)
    {
        var observations = new List<object>();
        try
        {
            var editRegression = Option("--smoke-mode") == "edit" ? await RunEditorRegressionAsync() : null;
            var motionRegression = Option("--smoke-mode") == "motion" ? await RunMotionRegressionAsync() : null;
            foreach (var page in new[] { "library", "projects", "context", "help", "agents", "connections", "settings" })
            {
                if (page == "agents")
                {
                    SelectPage(page);
                    AgentsPath.Text = Path.Combine(dataRoot, "smoke-AGENTS.md");
                    await ReadAgentsAsync();
                }
                else await NavigateAsync(page);
                Root.UpdateLayout();
                await Task.Delay(100);
                var element = pages[page];
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0) throw new InvalidOperationException($"页面没有有效布局：{page}");
                if (page != "library") await RenderSnapshotAsync(reportPath + "." + page + ".png");
                var scrollContent = (element as Microsoft.UI.Xaml.Controls.ScrollViewer)?.Content as FrameworkElement;
                var origin = scrollContent?.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
                var pageOrigin = element.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
                if (scrollContent is not null && origin is { } position &&
                    (position.X < pageOrigin.X - 1 || position.X + scrollContent.ActualWidth > pageOrigin.X + element.ActualWidth + 1))
                    throw new InvalidOperationException($"页面内容超出窗口：{page}");
                observations.Add(new { page, width = element.ActualWidth, height = element.ActualHeight, visible = element.Visibility == Visibility.Visible,
                    contentWidth = scrollContent?.ActualWidth, contentDesiredWidth = scrollContent?.DesiredSize.Width, contentX = origin?.X });
            }
            await NavigateAsync("library");
            if (ArtifactList.Items.Count > 0) ArtifactList.SelectedIndex = 0;
            await RenderSnapshotAsync(reportPath + ".png");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { status = "native-pages-loaded", uiThread = DispatcherQueue.HasThreadAccess, pages = observations, artifacts = ArtifactList.Items.Count, iconCornerRadius = BrandIcon.CornerRadius.TopLeft,
                headerHeight = Root.RowDefinitions[0].Height.Value, rootRows = Root.RowDefinitions.Count, editRegression, motionRegression, dialogs = smokeDialogs,
                windowCorners = new { supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), applyResult = cornerPreferenceResult, actualPreference = WindowAppearance.ReadCornerPreference(this) } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(reportPath + ".error.txt", ex.ToString()); }
        finally { Close(); }
    }

    private async Task RenderSnapshotAsync(string path, FrameworkElement? element = null)
    {
        Root.UpdateLayout();
        await Task.Delay(250);
        Root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element ?? Root);
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        stream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
