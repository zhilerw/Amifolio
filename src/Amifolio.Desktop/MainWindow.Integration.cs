using System.Diagnostics;
using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private CodexIntegrationService integration=null!;
    private async void IntegrationScopeChanged(object sender,SelectionChangedEventArgs e)
    {if(!ready||updating||operation is not null)return;await RunAsync("读取接入范围",async _=>{await LoadCollectionGrantStatusAsync();await RefreshEnvironmentAsync();});}
    private void InitializeIntegration()
    {
        // Native verification explicitly redirects all host writes away from the user's real configuration.
        var profile=Option("--profile-report") is not null;
        var isolated=Option("--smoke-report") is not null?Path.Combine(dataRoot,"smoke-codex-user"):profile?Path.Combine(dataRoot,"profile-codex-user"):null;
        var home=profile?Environment.GetEnvironmentVariable("CODEX_HOME"):isolated is null?null:Path.Combine(isolated,".codex");
        integration=new CodexIntegrationService(home,isolated);
        CodexConfigPath.Text=integration.ConfigPath;CodexSkillPath.Text=integration.SkillDirectory;
        BridgeCommand.Text=integration.McpSnippet(BridgeExecutable(),dataRoot);
    }
    private static TextBox PreviewBox(string text)=>new(){Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontFamily=(Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFontFamily"],FontSize=12,Width=540,Height=310};
    private void CopyText(string text,string status){var payload=new DataPackage();payload.SetText(text);Clipboard.SetContent(payload);StatusText.Text=status;}
    private async void ViewCodexConfig(object sender,RoutedEventArgs e)=>await RunAsync("读取 Codex 配置",async token=>{var snap=await integration.ReadConfigAsync(token);await ShowMessageAsync("config.toml",PreviewBox(snap.Exists?snap.Content[..Math.Min(snap.Content.Length,120000)]:"文件尚不存在，写入 MCP 时创建。"));});
    private async void OpenCodexConfig(object sender,RoutedEventArgs e)=>await RunAsync("打开 Codex 配置",async token=>{var snap=await integration.ReadConfigAsync(token);if(!snap.Exists){await ShowMessageAsync("文件不存在","可先点击“一键写入”，或自行创建 config.toml。");return;}Process.Start(new ProcessStartInfo("notepad.exe"){UseShellExecute=false,ArgumentList={integration.ConfigPath}});});
    private async void InstallMcp(object sender,RoutedEventArgs e)=>await RunAsync("配置 MCP",async token=>
    {
        if(!File.Exists(BridgeExecutable()))throw new IOException("桥接组件缺失，请使用完整便携包。");
        var snapshot=await integration.ReadConfigAsync(token);var snippet=integration.McpSnippet(BridgeExecutable(),dataRoot);
        var proposed=integration.PreviewMcpUpdate(snapshot,snippet);
        if(!await ConfirmAsync("配置 MCP 连接？",PreviewBox(integration.ConfigPath+"\n\n"+MakeDiff(snapshot.Content,proposed)),"备份并写入"))return;
        await integration.SaveMcpUpdateAsync(snapshot,proposed,token);StatusText.Text="MCP 已配置，请手动重新打开 Codex";
        await RefreshEnvironmentAsync();
        await ShowMessageAsync("配置已写入","请手动退出并重新打开 Codex，再检查 MCP 连接。");
    });
    private async Task<string> BundledSkillAsync(CancellationToken token)
    {
        var path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","codex","skills","amifolio","SKILL.md"));
        if(!File.Exists(path))throw new IOException("配套 skills 缺失，请使用完整便携包。");return await File.ReadAllTextAsync(path,token);
    }
    private void CopySkillPrompt(object sender,RoutedEventArgs e)=>CopyText(integration.SkillPrompt,"安装提示词已复制");
    private async void DownloadSkill(object sender,RoutedEventArgs e)=>await RunAsync("保存 skills 包",async token=>
    {
        var bytes=integration.SkillArchive(await BundledSkillAsync(token));var picker=new FileSavePicker(AppWindow.Id){SuggestedFileName="Amifolio-Codex-skills"};picker.FileTypeChoices.Add("ZIP 文件",[".zip"]);var file=await picker.PickSaveFileAsync();if(file is null)return;await File.WriteAllBytesAsync(file.Path,bytes,token);StatusText.Text="skills 包已保存";
    });
    private async void InstallSkill(object sender,RoutedEventArgs e)=>await RunAsync("安装 Codex skills",async token=>
    {
        var text=await BundledSkillAsync(token);
        var snapshot=await integration.ReadSkillAsync(token);
        if(!await ConfirmAsync(snapshot.Exists?"更新配套 skills？":"安装配套 skills？",PreviewBox(integration.SkillDirectory+"\n\n"+MakeDiff(snapshot.Content,text)),"备份并安装"))return;
        if(snapshot.Exists)await integration.ReplaceSkillAsync(snapshot,text,token);else await integration.InstallSkillAsync(text,token);
        StatusText.Text="skills 已安装，请重新打开 Codex 会话";
        await RefreshEnvironmentAsync();
    });
    private async void GoToAgents(object sender,RoutedEventArgs e)
    {
        if(operation is not null)return;var scope=SelectedScope(IntegrationScope)?.Id;
        if(!await RequestNavigationAsync("agents"))return;
        await RunAsync("读取部署范围",async _=>{updating=true;try{AgentsScope.SelectedItem=AgentsScope.Items.OfType<ScopeChoice>().First(x=>x.Id==scope);}finally{updating=false;}await LoadAgentsForScopeAsync();});
    }
    private string? DeployScope=>currentPage=="agents"?SelectedScope(AgentsScope)?.Id:SelectedScope(IntegrationScope)?.Id;
    private string DeployPath(string? project)
    {
        if(project is null)return Path.Combine(integration.CodexHome,"AGENTS.md");
        var root=projects.FirstOrDefault(p=>p.Id==project)?.RootPath;if(string.IsNullOrWhiteSpace(root))throw new IOException("项目未绑定目录。");return Path.Combine(root,"AGENTS.md");
    }
    private async void CopyAgentsPrompt(object sender,RoutedEventArgs e)=>await RunAsync("准备标准提示词",async token=>
    {
        var scope=DeployScope;var policy=await context.Learning.PolicyAsync(scope,token);var text=agents.PreviewManagedBlock(new(DeployPath(scope),"","",false),context.LearningEntryDirectory(scope),policy.Share);CopyText(text,"标准提示词已复制");
    });
    private async void DeployAgentsEntry(object sender,RoutedEventArgs e)
    {
        if(operation is not null)return;if(!await CanLeaveAgentsAsync())return;
        var scope=DeployScope;
        await RunAsync("配置风格与记忆入口",async token=>
        {
            var path=DeployPath(scope);var original=await agents.ReadAsync(path,token);var policy=await context.Learning.PolicyAsync(scope,token);await context.Learning.PublishProjectionAsync(scope,true,token);var proposed=agents.PreviewManagedBlock(original,context.LearningEntryDirectory(scope),policy.Share);
            if(!await ConfirmAsync("配置 AGENTS.md 入口？",PreviewBox(path+"\n\n"+MakeDiff(original.Content,proposed)),"备份并配置"))return;
            var backup=await agents.SaveAsync(original,proposed,token);var saved=await agents.ReadAsync(path,token);agentsUndo.Record(path,backup,saved.Hash);
            if(currentPage=="agents"){AgentsPath.Text=path;await ReadAgentsAsync();}
            await RefreshEnvironmentAsync();
            StatusText.Text=policy.Share?"风格与记忆入口已配置":"共享已关闭，对应托管入口已移除；已有规则保留";
        });
    }
    private async void InstallCollectionHook(object sender,RoutedEventArgs e)=>await RunAsync("安装结束钩子",async token=>
    {
        if(!File.Exists(BridgeExecutable()))throw new IOException("桥接组件缺失。");
        if(!await ConfirmAsync("安装 Codex 结束钩子？",integration.HooksPath+"\n保留已有事件。结束时只刷新Codex项目索引，不复制原文件。安装后需在Codex中检查并信任钩子。","安装"))return;
        await integration.InstallStopHookAsync(BridgeExecutable(),dataRoot,token);StatusText.Text="钩子已安装，请重新打开 Codex 并在设置中信任";
    });
}
