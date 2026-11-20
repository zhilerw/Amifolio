using Amifolio.Context;
using Amifolio.Core;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private bool loadingModelConfiguration;
    private Task remotePermissionUpdate=Task.CompletedTask;
    private async void RemotePermissionChanged(object sender,RoutedEventArgs e)
    {
        if(loadingModelConfiguration||!ready)return;var allowed=RemoteAllowed.IsOn;
        if(!allowed)CancelAutomaticProcessing();
        var previous=remotePermissionUpdate;remotePermissionUpdate=ApplyRemotePermissionAsync(previous,allowed);
        try{await remotePermissionUpdate;}catch(Exception){if(!closing)ModelSettingsStatus.Text="许可更新失败，请重试。";}
    }
    private async Task ApplyRemotePermissionAsync(Task previous,bool allowed)
    {
        try{await previous;}catch(Exception){}
        await new TaskStore(dataRoot).SetRemoteAllowedAsync(allowed);
        if(!closing)ModelSettingsStatus.Text=allowed?"已允许后台整合使用会话概述。":"远程发送已关闭，后台整合已停止。";
    }
    private async Task LoadModelConfigurationAsync(CancellationToken ct)
    {
        loadingModelConfiguration=true;
        try
        {
            var saved = await modelConfiguration.LoadAsync(ct);
            ModelEndpoint.Text = saved.Endpoint; ModelName.Text = saved.Model;
            ModelKey.Password = saved.ApiKey; RemoteAllowed.IsOn = saved.RemoteAllowed&&await new TaskStore(dataRoot).RemoteAllowedAsync(ct)!=false;
            if (saved.ApiKey.Length > 0) ModelSettingsStatus.Text = "已读取本机保存的配置。";
        }
        catch (IOException) { ModelSettingsStatus.Text = "无法读取已保存的配置，请重新填写并保存。"; }
        finally{loadingModelConfiguration=false;}
    }

    private async void SaveModelConfiguration(object sender, RoutedEventArgs e) => await RunAsync("正在保存配置", async ct =>
    {
        CancelAutomaticProcessing();
        await remotePermissionUpdate;
        await modelConfiguration.SaveAsync(new(ModelEndpoint.Text, ModelName.Text, RemoteAllowed.IsOn, ModelKey.Password), ct);
        await new TaskStore(dataRoot).SetRemoteAllowedAsync(RemoteAllowed.IsOn,ct);
        ModelSettingsStatus.Text = "已保存到本机。"; StatusText.Text = "配置已保存";
    });

    private async void ClearModelConfiguration(object sender, RoutedEventArgs e) => await RunAsync("正在清除配置", async ct =>
    {
        CancelAutomaticProcessing();
        await modelConfiguration.ClearAsync(ct);
        loadingModelConfiguration=true;
        try{ModelKey.Password = ""; ModelName.Text = ""; ModelEndpoint.Text = "https://api.openai.com/v1"; RemoteAllowed.IsOn = false;}
        finally{loadingModelConfiguration=false;}
        await remotePermissionUpdate;await new TaskStore(dataRoot).SetRemoteAllowedAsync(false,ct);
        ModelSettingsStatus.Text = "已清除保存的配置。"; StatusText.Text = "已清除配置";
    });
}
