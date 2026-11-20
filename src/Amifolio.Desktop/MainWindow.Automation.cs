using Amifolio.Context;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer automationTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<string?> automaticScopes = [];
    private CancellationTokenSource? automaticRequest;
    private bool automationTickRunning;
    private sealed record AutomationControls(StackPanel Panel, ToggleSwitch Learning, ToggleSwitch Summary, ToggleSwitch Remote);

    private AutomationControls CreateAutomationControls(AutomationStatus state)
    {
        var learning = new ToggleSwitch { Header = "自动生成记忆与风格候选", IsOn = state.Options.LearningEnabled, OnContent = "开启", OffContent = "关闭" };
        var summary = new ToggleSwitch { Header = "自动生成概要与总结候选", IsOn = state.Options.SummaryEnabled, OnContent = "开启", OffContent = "关闭" };
        var remote = new ToggleSwitch { Header = "允许此作用域自动发送片段", IsOn = state.Options.RemoteAllowed, OnContent = "开启", OffContent = "关闭" };
        var panel = new StackPanel { Spacing = 14, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = "仅使用已解析且有效的片段。候选需确认，不覆盖人工内容。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(learning); panel.Children.Add(summary); panel.Children.Add(remote);
        panel.Children.Add(new TextBlock { Text = $"应用打开时使用已保存的 AI 配置。自动请求全应用每天最多 {state.DailyLimit} 次，至少间隔 60 秒；失败不自动重试。可能产生服务费用。共享和原件路径另行控制。", Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"今日自动请求 {state.RequestsToday}/{state.DailyLimit} · {AutomationStatusLabel(state.Status)}", Style = (Style)Application.Current.Resources["Caption"] });
        return new(panel, learning, summary, remote);
    }

    private static string AutomationStatusLabel(string value) => value switch
    {
        "disabled" => "自动处理已关闭", "unchanged" => "片段未变化", "no_evidence" or "noevidence" => "等待已解析片段",
        "cooldown" => "等待下次检查", "budget_exhausted" or "budget" => "今日限额已用完", "generated" or "completed" => "候选已生成",
        "failed" => "上次未完成，请查看设置中的日志", "permission_required" => "等待发送许可", "running" => "正在处理",
        "provider_http" => "模型服务请求未完成", "provider_invalid" => "模型返回格式不符", "configuration_invalid" => "请检查 AI 配置",
        "sources_changed" => "来源已变化，请核对后重新生成", "cancelled" => "已取消", "interrupted" => "处理已中断", "processing_failed" => "处理失败，请查看设置中的日志",
        _ => "等待检查"
    };

    private async void ConfigureAutomation(object sender, RoutedEventArgs e)
    {
        if (!await CanLeaveContextAsync()) return;
        var project = ContextProjectId;
        await RunAsync("自动处理设置", async ct =>
        {
            var controls = CreateAutomationControls(await context.ReadAutomationAsync(project, ct));
            if (!await ConfirmAsync("自动处理", controls.Panel, "保存")) return;
            await SaveAutomationControlsAsync(project, controls, ct);
            StatusText.Text = "自动处理设置已保存";
        });
    }

    private async Task SaveAutomationControlsAsync(string? project, AutomationControls controls, CancellationToken ct)
    {
        CancelAutomaticProcessing();
        await context.SetAutomationAsync(project, new(controls.Learning.IsOn, controls.Summary.IsOn, controls.Remote.IsOn), ct);
        automaticScopes = await context.ListAutomaticScopesAsync(ct);
        await RefreshAutomationSummaryAsync();
    }

    private async Task RefreshAutomationSummaryAsync()
    {
        var project = ContextProjectId;
        var revision = contextUiRevision;
        var state = await context.ReadAutomationAsync(project, backgroundTasks.Token);
        if (closing || project != ContextProjectId || revision != contextUiRevision) return;
        AutomationSummary.Text = !state.Options.LearningEnabled && !state.Options.SummaryEnabled
            ? "自动处理关闭" : $"自动候选 · 今日 {state.RequestsToday}/{state.DailyLimit} · {AutomationStatusLabel(state.Status)}";
    }

    private void UpdateContextDocumentEnabledControl()
    {
        var name = ContextDocument.SelectedItem as string;
        ContextDocumentEnabledButton.Visibility = name is "overview.md" or "summary.md" or "memory.md" or "style.md" ? Visibility.Visible : Visibility.Collapsed;
        ContextDocumentEnabledButton.Content = name is not null && contextSnapshot?.DisabledDocuments.Contains(name) == true ? "启用文档" : "停用文档";
    }

    private async void ToggleContextDocumentEnabled(object sender, RoutedEventArgs e)
    {
        if (ContextDocument.SelectedItem is not string name || !await CanLeaveContextAsync()) return;
        var project = ContextProjectId;
        var enabled = contextSnapshot?.DisabledDocuments.Contains(name) == true;
        await RunAsync(enabled ? "启用文档" : "停用文档", async ct =>
        {
            await context.SetDocumentEnabledAsync(project, name, enabled, ct);
            await LoadContextAsync();
            StatusText.Text = enabled ? "文档已启用" : "文档已停用，共享入口已更新";
        });
    }

    private async Task StartAutomaticProcessingAsync()
    {
        try
        {
            automaticScopes = await context.ListAutomaticScopesAsync(backgroundTasks.Token);
            automationTimer.Tick += AutomaticProcessingTick;
            automationTimer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closing) StatusText.Text = "自动处理未启动，可在“自动处理”中重试。"; }
    }

    private void CancelAutomaticProcessing(){automaticRequest?.Cancel();learningRequest?.Cancel();}
    private void StopAutomaticProcessing()
    {
        StopLearningProcessing();
        automationTimer.Stop();
        automationTimer.Tick -= AutomaticProcessingTick;
        CancelAutomaticProcessing();
    }

    private async void AutomaticProcessingTick(object? sender, object e)
    {
        if (closing || !ready || automationTickRunning || operation is not null || contextEdit.IsEditing || automaticScopes.Count == 0 || !RemoteAllowed.IsOn) return;
        automationTickRunning = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(backgroundTasks.Token);
        automaticRequest = request;
        try
        {
            await remotePermissionUpdate;
            var saved = await modelConfiguration.LoadAsync(request.Token);
            if (!saved.RemoteAllowed || string.IsNullOrWhiteSpace(saved.ApiKey) || string.IsNullOrWhiteSpace(saved.Model)) return;
            foreach (var scope in automaticScopes)
            {
                request.Token.ThrowIfCancellationRequested();
                if (operation is not null || contextEdit.IsEditing || !RemoteAllowed.IsOn) break;
                var changed = await context.RunAutomaticAsync(scope, new(saved.Endpoint, saved.Model, true), saved.ApiKey, request.Token);
                if (changed && !closing)
                {
                    StatusText.Text = "自动候选已生成，可在“风格与记忆”中确认";
                    // Never replace a draft opened while the remote request was running.
                    if (currentPage == "context" && ContextProjectId == scope && !contextEdit.IsEditing && operation is null) await LoadContextAsync(background: true);
                }
            }
            if (!closing && currentPage == "context" && !contextEdit.IsEditing) await RefreshAutomationSummaryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closing) StatusText.Text = "自动候选未完成，请在“任务”中查看；不会自动重试。"; }
        finally { if (ReferenceEquals(automaticRequest, request)) automaticRequest = null; automationTickRunning = false; }
    }
}
