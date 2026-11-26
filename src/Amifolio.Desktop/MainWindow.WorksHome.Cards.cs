using System.Runtime.InteropServices;
using Amifolio.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record WorksHomeCardEntry(CodexWorkCard Card, string ProjectName);

    private WorksHomeCardEntry[] displayedWorksHomeCards = [];
    private CancellationTokenSource? worksHomeThumbnails;
    private readonly SemaphoreSlim worksHomeThumbnailSlots = new(2);

    private void InitializeWorksHomeCards()
    {
        worksHomeList.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <VariableSizedWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="1" ItemHeight="255" />
            </ItemsPanelTemplate>
            """);
        worksHomeList.Loaded += (_, _) => UpdateWorksHomeCardColumns();
        worksHomeList.SizeChanged += (_, _) => UpdateWorksHomeCardColumns();
    }

    private void UpdateWorksHomeCardColumns()
    {
        if (worksHomeList.ItemsPanelRoot is not VariableSizedWrapGrid gallery || worksHomeList.ActualWidth < 1)
            return;
        var columns = worksHomeList.ActualWidth >= 680 ? 2 : 1;
        gallery.MaximumRowsOrColumns = columns;
        gallery.ItemWidth = Math.Floor(worksHomeList.ActualWidth / columns);
    }

    private void ShowWorksHomeCards(WorksHomeCardEntry[] entries)
    {
        if (displayedWorksHomeCards.SequenceEqual(entries)) return;

        var selectedId = ((worksHomeList.SelectedItem as GridViewItem)?.Tag as WorksHomeCardEntry)?.Card.Work.Id;
        displayedWorksHomeCards = entries;
        worksHomeThumbnails?.Cancel();
        worksHomeThumbnails?.Dispose();
        worksHomeThumbnails = CancellationTokenSource.CreateLinkedTokenSource(backgroundTasks.Token);
        var token = worksHomeThumbnails.Token;
        worksHomeList.SelectedItem = null;
        worksHomeList.Items.Clear();
        GridViewItem? selected = null;
        foreach (var entry in entries)
        {
            var row = CreateWorksHomeCard(entry, token);
            worksHomeList.Items.Add(row);
            if (entry.Card.Work.Id == selectedId) selected = row;
        }
        if (selected is not null) worksHomeList.SelectedItem = selected;
    }

    private GridViewItem CreateWorksHomeCard(WorksHomeCardEntry entry, CancellationToken token)
    {
        var work = entry.Card.Work;
        var metadata = entry.Card.SessionTitle is { } sessionTitle
            ? $"{sessionTitle} · {work.SourceLabel} · {entry.ProjectName}"
            : $"{work.SourceLabel} · {entry.ProjectName}";
        var caption = WorksHomeCaption(metadata);
        caption.MaxLines = 1;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(caption, metadata);
        var date = WorksHomeCaption(work.RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        var title = new TextBlock
        {
            Text = work.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis
        };
        var heading = new StackPanel { Spacing = 3, Padding = new Thickness(12, 9, 12, 10) };
        heading.Children.Add(title);
        heading.Children.Add(caption);
        heading.Children.Add(date);
        var card = new StackPanel { Background = WorksHomeBrush("Paper") };
        card.Children.Add(CreateWorksHomeCover(entry.Card, token));
        card.Children.Add(heading);

        var row = new GridViewItem
        {
            Content = card, Tag = entry, Padding = new Thickness(3), Margin = new Thickness(0, 0, 9, 9),
            MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(row, $"{work.Title}，{metadata}，{date.Text}");
        AutomationProperties.SetAutomationId(row, $"WorkCard_{work.Id}");
        return row;
    }

    private Border CreateWorksHomeCover(CodexWorkCard card, CancellationToken token)
    {
        var (format, background, foreground) = WorksHomeCoverStyle(card.Work.Title);
        var backdrop = new SolidColorBrush(background);
        var ink = new SolidColorBrush(foreground);
        var cover = new Grid { Background = backdrop };
        var frame = new Border { Height = 138, Child = cover };
        var hasExcerpt = card.Excerpt.Length > 0;
        var content = new StackPanel { Spacing = 6, Margin = new Thickness(13, 11, 13, 10) };
        var label = new TextBlock
        {
            Text = format, Foreground = ink, FontSize = hasExcerpt ? 12 : 31,
            FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis
        };
        content.Children.Add(label);
        var description = new TextBlock
        {
            Text = hasExcerpt ? card.Excerpt : WorksHomeFallbackText(card.Work),
            Foreground = ink, FontSize = 13, MaxLines = hasExcerpt ? 5 : 2,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetAutomationId(description, $"WorkCoverText_{card.Work.Id}");
        content.Children.Add(description);
        if (!hasExcerpt)
        {
            var stripe = new Rectangle
            {
                Width = 38, Fill = ink, Opacity = .13, HorizontalAlignment = HorizontalAlignment.Right,
                IsHitTestVisible = false
            };
            var rule = new Rectangle
            {
                Height = 3, Fill = ink, Opacity = .32, VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false
            };
            cover.Children.Add(stripe);
            cover.Children.Add(rule);
            content.VerticalAlignment = VerticalAlignment.Center;
        }
        cover.Children.Add(content);

        if (card.HasImage)
        {
            var started = false;
            var inViewport = false;
            frame.EffectiveViewportChanged += async (_, e) =>
            {
                var viewport = e.EffectiveViewport;
                inViewport = viewport.Height > 0 && viewport.Width > 0 && viewport.Bottom > 0 &&
                    viewport.Top < frame.ActualHeight && viewport.Right > 0 && viewport.Left < frame.ActualWidth;
                if (started || !CanLoad()) return;
                started = true;
                try
                {
                    await worksHomeThumbnailSlots.WaitAsync(token);
                    try
                    {
                        if (!CanLoad()) { started = false; return; }
                        var bitmap = await ReadWorksHomeImageAsync(card, 600, token);
                        if (bitmap is not null && !token.IsCancellationRequested && !closing)
                        {
                            var preview = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                            AutomationProperties.SetName(preview, $"{card.Work.Title}的预览");
                            AutomationProperties.SetAutomationId(preview, $"WorkCoverImage_{card.Work.Id}");
                            frame.Child = preview;
                        }
                    }
                    finally { worksHomeThumbnailSlots.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            };

            bool CanLoad() => inViewport && frame.IsLoaded && !closing && currentPage == "works" &&
                !token.IsCancellationRequested;
        }
        return frame;
    }

    private static string WorksHomeFallbackText(CodexWork work) => work.Availability switch
    {
        "仅来源会话" => "可在来源会话查看",
        "引用不可用" or "源文件缺失" or "源文件不可读" => "原件不可用",
        var state when state.StartsWith("源文件已变化", StringComparison.Ordinal) => "原件已更新",
        _ => "暂无预览"
    };

    private static (string Label, Windows.UI.Color Background, Windows.UI.Color Foreground) WorksHomeCoverStyle(string title)
    {
        var extension = Path.GetExtension(title).ToLowerInvariant();
        var label = extension.Length is > 1 and <= 9 ? extension[1..].ToUpperInvariant() : "文件";
        return extension switch
        {
            ".png" or ".jpeg" or ".jpg" or ".bmp" or ".gif" or ".webp" or ".svg" or ".tiff" or ".avif" =>
                (label, Microsoft.UI.ColorHelper.FromArgb(255, 29, 109, 96), Microsoft.UI.Colors.White),
            ".pdf" or ".doc" or ".docx" or ".ppt" or ".pptx" =>
                (label, Microsoft.UI.ColorHelper.FromArgb(255, 229, 231, 221), Microsoft.UI.ColorHelper.FromArgb(255, 51, 61, 54)),
            ".xlsx" or ".xls" or ".csv" or ".tsv" =>
                (label, Microsoft.UI.ColorHelper.FromArgb(255, 204, 220, 195), Microsoft.UI.ColorHelper.FromArgb(255, 38, 55, 37)),
            ".md" or ".txt" or ".html" or ".htm" or ".json" or ".cs" or ".js" or ".py" =>
                (label, Microsoft.UI.ColorHelper.FromArgb(255, 218, 234, 226), Microsoft.UI.ColorHelper.FromArgb(255, 37, 71, 63)),
            _ => (label, Microsoft.UI.ColorHelper.FromArgb(255, 230, 228, 217), Microsoft.UI.ColorHelper.FromArgb(255, 42, 43, 39))
        };
    }

    private Task<BitmapImage?> ReadWorksHomeImageAsync(CodexWorkCard card, int pixelLimit, CancellationToken token) =>
        ReadBoundedCardImageAsync(() => SourceStore.OpenCodexWorkImageAsync(card.Work, card.ImageModifiedAt, token), pixelLimit, token);

    private static async Task<BitmapImage?> ReadBoundedCardImageAsync(Func<Task<Stream?>> open, int pixelLimit, CancellationToken token)
    {
        try
        {
            using var input = await open();
            if (input is null) return null;
            using var stream = input.AsRandomAccessStream();
            token.ThrowIfCancellationRequested();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0 || (ulong)width * height > 48_000_000) return null;
            stream.Seek(0);
            var bitmap = new BitmapImage();
            if (width >= height) bitmap.DecodePixelWidth = Math.Min((int)width, pixelLimit);
            else bitmap.DecodePixelHeight = Math.Min((int)height, pixelLimit);
            await bitmap.SetSourceAsync(stream).AsTask(token);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    private async Task PreviewWorksHomeOriginalAsync(CodexWorkCard card, ArtifactRecord artifact,
        Func<bool> stillSelected, CancellationToken ct)
    {
        var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
        var bitmap = card.HasImage ? await ReadWorksHomeImageAsync(card, 960, ct) : null;
        if (bitmap is not null)
        {
            content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 360 });
        }
        else if (!string.IsNullOrWhiteSpace(artifact.Preview))
        {
            var excerpt = artifact.Preview[..Math.Min(artifact.Preview.Length, 4000)];
            content.Children.Add(new TextBlock { Text = excerpt, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        else
        {
            var reason = artifact.ExtractionStatus;
            var hint = reason is "待提取" or "已取消" || reason.StartsWith("提取", StringComparison.Ordinal) ||
                reason.StartsWith("不支持提取", StringComparison.Ordinal) || reason.StartsWith("文件超过", StringComparison.Ordinal)
                ? $"{reason}，可打开原件查看。"
                : "这份作品暂无预览，可打开原件查看。";
            content.Children.Add(WorksHomeCaption(hint));
        }

        var original = WorksHomeCaption(artifact.ObjectPath);
        original.IsTextSelectionEnabled = true;
        content.Children.Add(original);
        ct.ThrowIfCancellationRequested();
        if (stillSelected())
        {
            StatusText.Text = "就绪";
            await ShowMessageAsync(artifact.Title, new ScrollViewer { Content = content, MaxHeight = 470 });
        }
    }
}
