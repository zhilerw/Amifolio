using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private Task? environmentInspection;
    private int environmentVersion;
    private string? environmentRequestedScope;
    private bool environmentRequestedDetails;
    private CodexPluginState pluginConnectionState = CodexPluginState.Unknown;
    private IReadOnlyList<string> pluginLegacyEntries = [];

    private async Task<bool> IsCreativePluginConfiguredAsync(CancellationToken ct) =>
        await integration.ReadPluginStateAsync(ct) == CodexPluginState.Enabled;

    private Task RefreshEnvironmentAsync(bool force = false, bool showDetails = false)
    {
        var scope = SelectedScope(IntegrationScope)?.Id;
        var details = showDetails || MigrationCompatibility.IsExpanded;
        if (!force && environmentInspection is { IsCompleted: false } && environmentRequestedScope == scope &&
            (environmentRequestedDetails || !details)) return environmentInspection;
        environmentRequestedScope = scope;
        environmentRequestedDetails = details;
        var version = ++environmentVersion;
        environmentInspection = InspectEnvironmentCoreAsync(scope, version, details);
        return environmentInspection;
    }

    private async Task InspectEnvironmentCoreAsync(string? scope, int version, bool details)
    {
        var projectRoot = scope is null ? null : projects.FirstOrDefault(p => p.Id == scope)?.RootPath;
        var projectAgentsPath = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.Combine(projectRoot, "AGENTS.md");
        var token = backgroundTasks.Token;
        var statusRead = false;
        try
        {
            var inspection = await Task.Run(() => integration.InspectPluginConnectionAsync(projectAgentsPath, token), token);
            if (closing || version != environmentVersion) return;
            ShowPluginConnectionStatus(inspection);
            statusRead = true;
            if (!details) return;

            var skill = await BundledSkillAsync(token);
            var path = DeployPath(scope);
            var checks = await Task.Run(() => integration.InspectEnvironmentAsync(BridgeExecutable(), dataRoot, skill, path, token), token);
            if (closing || version != environmentVersion) return;
            EnvironmentChecks.ItemsSource = checks;
            EnvironmentSummary.Text = "已核对本机文件。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (closing || version != environmentVersion) return;
            if (!statusRead)
                ShowPluginConnectionStatus(new(CodexPluginState.Unknown, [], true));
            EnvironmentChecks.ItemsSource = null;
            EnvironmentSummary.Text = "无法完成兼容检查。请核对部署范围和随包组件后重试。";
        }
    }

    private void ShowPluginConnectionStatus(PluginConnectionInspection inspection)
    {
        pluginConnectionState = inspection.PluginState;
        pluginLegacyEntries = inspection.LegacyEntries;
        PluginConnectionSummary.Text = pluginConnectionState switch
        {
            CodexPluginState.Enabled => "本机配置已启用 Amifolio 插件。实际连接待在 Codex 会话中确认。",
            CodexPluginState.Disabled => "本机配置已停用 Amifolio 插件。",
            CodexPluginState.NotConfigured => "本机配置中未发现 Amifolio 插件。",
            _ => "无法确认本机插件配置。"
        };
        PluginConnectionAction.Content = pluginConnectionState == CodexPluginState.NotConfigured ? "安装插件" : "检查连接";
        PluginConnectionAction.IsEnabled = true;
        PluginMigrationNotice.Text = pluginLegacyEntries.Count > 0
            ? $"检测到旧版接入：{string.Join("、", pluginLegacyEntries)}。切换到插件前，请在「迁移与兼容」核对这些配置。"
            : "部分旧版配置无法核对。可展开「迁移与兼容」检查。";
        PluginMigrationNotice.Visibility = pluginLegacyEntries.Count > 0 || inspection.LegacyInspectionIncomplete
            ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentBadge.Visibility = PluginMigrationNotice.Visibility;
    }

    private async void OpenMigrationCompatibility(Expander sender, ExpanderExpandingEventArgs e)
    {
        if (ready) await RefreshEnvironmentAsync(showDetails: true);
    }

    private async void InspectEnvironment(object sender, RoutedEventArgs e) =>
        await RefreshEnvironmentAsync(force: true, showDetails: true);
}
