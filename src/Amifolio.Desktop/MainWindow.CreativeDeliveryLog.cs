using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task ShowCreativeDeliveryLogAsync(string? projectId)
    {
        if (closing || currentPage != "creative" || creativeBusy || creativeUpdatingScope) return;
        await RunAsync("读取提供日志", ct => RunCreativeActionAsync(async () =>
        {
            var entries = await CreativePreferences.ReadCreativeDeliveryLogAsync(projectId, ct);
            if (closing) return;
            StatusText.Text = "就绪";
            var scope = loadedCreativeScope;
            if (currentPage != "creative" || scope?.Id != projectId || Root.XamlRoot is null) return;

            var caption = (Style)Application.Current.Resources["Caption"];
            var content = new StackPanel { Spacing = 8, MaxWidth = 480 };
            content.Children.Add(new TextBlock
            {
                Text = scope?.Id is null ? "所有项目" : "项目 · " + scope.Name,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new TextBlock
            {
                Text = "记录可能涉及全局默认或项目偏好。确认表示 Codex 的结束钩子匹配到了该回合。",
                Style = caption,
                TextWrapping = TextWrapping.Wrap
            });

            if (entries.Count == 0)
            {
                content.Children.Add(new TextBlock { Text = "暂无提供记录。" });
            }
            else
            {
                var rows = new StackPanel { Spacing = 12 };
                foreach (var entry in entries)
                {
                    var name = projects.FirstOrDefault(project => project.Id == entry.ProjectId)?.Name
                        ?? entry.ProjectId;
                    var row = new StackPanel { Spacing = 2 };
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{entry.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {name}",
                        TextWrapping = TextWrapping.Wrap
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = entry.Confirmed ? "匹配回合已确认" : "已生成上下文 · 回合未确认",
                        TextWrapping = TextWrapping.Wrap
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = "会话 · " + entry.SessionId,
                        Style = caption,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                    rows.Children.Add(row);
                }
                content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 340 });
            }

            await ShowMessageAsync("创作偏好提供日志", content);
        }));
    }
}
