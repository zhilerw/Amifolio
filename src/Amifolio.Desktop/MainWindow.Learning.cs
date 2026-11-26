using Amifolio.Core;
using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record LearningRow(object Item,string Title,string Detail,string State);
    private LearningSnapshot? learningSnapshot;
    private LearningEntry? learningEntry;
    private string? loadedLearningScope;
    private string learningReturnPage="context";
    private bool loadingLearning,editingLearning,learningUiBusy,editingLearningSettings;
    private long learningDraftRevision;
    private Button LearningSettingsCancel=>(Button)LearningPage.FindName("CancelSettingsButton");
    private string learningEntryBefore="";
    private string learningEndpoint="";
    private string learningSavedEndpoint="";
    private int learningKindIndex,learningStateIndex;
    private long learningViewRequest;
    private bool LearningSettingsDirty=>learningSnapshot is not null&&!loadingLearning&&LearningOptions(true)!=learningSnapshot.Policy;
    private string? LearningScopeId=>SelectedScope(LearningPage.Scope)?.Id;

    private void InitializeLearningPage()
    {
        LearningPage.Back.Click+=async(_,_)=>await RequestNavigationAsync(learningReturnPage);
        LearningPage.Refresh.Click+=async(_,_)=>await LearningUiAsync(()=>RefreshLearningPageAsync(true));
        LearningPage.History.Click+=async(_,_)=>await OpenLearningHistoryAsync();
        LearningPage.Scope.SelectionChanged+=LearningScopeChanged;
        LearningPage.Kind.SelectionChanged+=async(_,_)=>await LearningFilterChangedAsync();
        LearningPage.StateFilter.SelectionChanged+=async(_,_)=>await LearningFilterChangedAsync();
        LearningPage.Rows.SelectionChanged+=LearningSelectionChanged;
        LearningPage.Edit.Click+=async(_,_)=>await LearningUiAsync(EditLearningEntryAsync);
        LearningPage.Cancel.Click+=(_,_)=>{if(!learningUiBusy)CancelLearningEntry();};
        LearningPage.ToggleState.Click+=async(_,_)=>await LearningUiAsync(ToggleLearningEntryAsync);
        LearningPage.Delete.Click+=async(_,_)=>await LearningUiAsync(DeleteLearningItemAsync);
        LearningPage.Undo.Click+=async(_,_)=>await LearningUiAsync(UndoLearningEntryAsync);
        LearningPage.Retry.Click+=async(_,_)=>await LearningUiAsync(RetryLearningJobAsync);
        LearningPage.SaveSettings.Click+=async(_,_)=>
        {
            if(!editingLearningSettings){editingLearningSettings=true;learningDraftRevision++;UpdateLearningControls();}
            else await LearningUiAsync(SaveLearningSettingsAsync);
        };
        LearningSettingsCancel.Click+=(_,_)=>{if(!learningUiBusy)CancelLearningSettings();};
        LearningPage.Process.Click+=async(_,_)=>await LearningUiAsync(ProcessLearningNowAsync);
        foreach(var box in new[]{LearningPage.Capture,LearningPage.Learn,LearningPage.Activate,LearningPage.Share,LearningPage.Contribute,LearningPage.Remote})
        {box.Checked+=(_,_)=>UpdateLearningControls();box.Unchecked+=(_,_)=>UpdateLearningControls();}
        LearningPage.Remote.Checked+=(_,_)=>{if(!loadingLearning){learningEndpoint=learningSavedEndpoint;UpdateLearningControls();}};
        LearningPage.Mode.SelectionChanged+=(_,_)=>UpdateLearningControls();
        LearningPage.Batch.ValueChanged+=(_,_)=>UpdateLearningControls();
    }
    private async void OpenLearning(object sender,RoutedEventArgs e)=>await OpenLearningPageAsync(false);
    private async void OpenLearningLogs(object sender,RoutedEventArgs e)=>await OpenLearningPageAsync(true);
    private async Task<bool> OpenLearningPageAsync(bool logs,string? selectedScope=null,bool explicitScope=false,string? returnPage=null)
    {
        var scope=explicitScope?selectedScope:currentPage=="connections"?SelectedScope(IntegrationScope)?.Id:loadedLearningScope;
        var origin=returnPage ?? (explicitScope?"context":currentPage=="learning"?learningReturnPage:currentPage);
        if(currentPage=="learning"&&!await CanLeaveLearningAsync())return false;
        if(!await RequestNavigationAsync("learning"))return false;
        learningReturnPage=origin;SelectPage("learning");
        loadingLearning=true;
        try
        {
            SetScopes(LearningPage.Scope,"全局偏好");
            LearningPage.Scope.SelectedItem=LearningPage.Scope.Items.OfType<ScopeChoice>().FirstOrDefault(s=>s.Id==scope)??LearningPage.Scope.Items[0];
            LearningPage.Kind.SelectedIndex=logs?1:0;
        }
        finally{loadingLearning=false;}
        await LearningUiAsync(()=>RefreshLearningPageAsync());
        return true;
    }
    private LearningPolicy LearningOptions(bool useLoadedScope=false)
    {
        var old=learningSnapshot?.Policy??new();var global=(useLoadedScope?loadedLearningScope:LearningScopeId) is null;
        return old with
        {
            Capture=!global&&LearningPage.Capture.IsChecked==true,Learn=LearningPage.Learn.IsChecked==true,
            AutoActivate=LearningPage.Activate.IsChecked==true,Share=LearningPage.Share.IsChecked==true,
            ContributeGlobal=!global&&LearningPage.Contribute.IsChecked==true,
            Mode=global||LearningPage.Mode.SelectedIndex!=1?"chat":"background",
            BatchSize=global?old.BatchSize:double.IsNaN(LearningPage.Batch.Value)?5:(int)Math.Clamp(LearningPage.Batch.Value,1,20),
            Remote=!global&&LearningPage.Mode.SelectedIndex==1&&LearningPage.Remote.IsChecked==true,
            Endpoint=!global&&LearningPage.Mode.SelectedIndex==1&&LearningPage.Remote.IsChecked==true?learningEndpoint:old.Endpoint
        };
    }
    private async Task RefreshLearningPageAsync(bool preserveDraft=false)
    {
        var scope=LearningScopeId;var request=++learningViewRequest;var draftRevision=learningDraftRevision;
        var keepDraft=preserveDraft&&(editingLearning||editingLearningSettings);
        var snapshot=await context.Learning.SnapshotAsync(scope,backgroundTasks.Token);
        if(closing||request!=learningViewRequest||scope!=LearningScopeId)return;
        loadedLearningScope=scope;
        var saved=await modelConfiguration.LoadAsync(backgroundTasks.Token);
        if(closing||request!=learningViewRequest||scope!=LearningScopeId)return;
        keepDraft|=preserveDraft&&(draftRevision!=learningDraftRevision||editingLearning||editingLearningSettings);
        if(!keepDraft)
        {
            loadingLearning=true;
            try
            {
                learningSnapshot=snapshot;learningSavedEndpoint=saved.Endpoint;learningEndpoint=snapshot.Policy.Remote&&snapshot.Policy.Endpoint.Length>0?snapshot.Policy.Endpoint:saved.Endpoint;
                var p=snapshot.Policy;
                LearningPage.Capture.IsChecked=p.Capture;LearningPage.Learn.IsChecked=p.Learn;LearningPage.Activate.IsChecked=p.AutoActivate;
                LearningPage.Share.IsChecked=p.Share;LearningPage.Contribute.IsChecked=p.ContributeGlobal;LearningPage.Mode.SelectedIndex=p.Mode=="background"?1:0;
                LearningPage.Batch.Value=p.BatchSize;LearningPage.Remote.IsChecked=p.Remote;
            }
            finally{loadingLearning=false;}
            await RenderLearningRowsAsync();
        }
        LearningPage.Service.Text=string.IsNullOrWhiteSpace(saved.ApiKey)?"后台未配置；会话内模式无需Key。":"所选服务："+(Uri.TryCreate(saved.Endpoint,UriKind.Absolute,out var uri)?uri.Host:"地址无效")+" · "+saved.Model;
        LearningPage.Status.Text=$"{LearningStatusLabel(snapshot.Status)} · 待整合 {snapshot.Pending} · 待核验 {snapshot.WaitingEvidence} · 需重试 {snapshot.Failed}";
        LearningPage.Coverage.Text=scope is null?"仅提炼已逐项目授权参与的通用偏好，不共享项目事实或原文。":"采集和自动启用需本项目授权；工具、进度和子Agent事件不重复计数。";
        var capacity=await context.Learning.CapacityAsync(backgroundTasks.Token);
        if(closing||request!=learningViewRequest||scope!=LearningScopeId)return;
        LearningPage.Capacity.Text=$"全库常用概述 {capacity.HotReceipts}/{capacity.ReceiptHotLimit} · 画像 {capacity.HotProfiles}/{capacity.ProfileHotLimit} · 历史 {capacity.ColdReceipts+capacity.ColdProfiles}";
        UpdateLearningControls();
        if(scope is not null&&snapshot.Policy.Capture&&!ShowingBrowserCache)
        {
            var turns=await context.RecentLearningTurnsAsync(scope,integration.CodexHome,backgroundTasks.Token);
            var missing=await context.Learning.MissingReceiptsAsync(scope,turns,backgroundTasks.Token);
            if(closing||request!=learningViewRequest||scope!=LearningScopeId)return;
            LearningPage.Coverage.Text=turns.Count==0?"尚无可核对的用户回合。宿主格式不支持时，概述保留待核验，不自动启用偏好。":$"近期核对 {turns.Count} 个回合 · 未见回执 {missing.Count} 个（有界范围，不代表完整历史）";
        }
    }
    private async Task RenderLearningRowsAsync()
    {
        if(learningSnapshot is null)return;var id=learningEntry?.Id;
        IEnumerable<LearningRow> rows=LearningPage.Kind.SelectedIndex switch
        {
            1=>learningSnapshot.Summaries.Where(s=>s.State!="deleted").Select(s=>new LearningRow(s,s.Summary,$"{s.RecordedAt.LocalDateTime:MM-dd HH:mm} · 回执 {s.Sequence} / v{s.Version} · {s.Outputs.Count} 项产物",LearningStateLabel(s.State))),
            2=>learningSnapshot.Jobs.Select(j=>new LearningRow(j,$"{(j.Mode=="chat"?"会话内":"后台")}整合 · {j.Summaries.Count} 个回合",LearningReason(j.Error),LearningStateLabel(j.State))),
            _=>learningSnapshot.Entries.Where(e=>e.State!="deleted").Where(e=>LearningPage.StateFilter.SelectedIndex switch{1=>e.State=="active",2=>e.State=="candidate",3=>e.State=="disabled",_=>true})
                .Select(e=>new LearningRow(e,e.Text,(e.Kind=="style"?"风格":e.Kind=="summary"?"总结":"记忆")+" · "+(e.Manual?"人工修订":LearningReason(e.Reason)),LearningStateLabel(e.State)))
        };
        loadingLearning=true;
        try{LearningPage.Rows.ItemsSource=rows.ToArray();LearningPage.Rows.SelectedItem=LearningPage.Rows.Items.OfType<LearningRow>().FirstOrDefault(r=>r.Item is LearningEntry entry&&entry.Id==id);}
        finally{loadingLearning=false;}
        if(!editingLearning)await ShowLearningSelectionAsync();
    }
    private async Task LearningFilterChangedAsync()
    {
        if(loadingLearning)return;
        if(!await CanLeaveLearningEntryAsync())
        {loadingLearning=true;try{LearningPage.Kind.SelectedIndex=learningKindIndex;LearningPage.StateFilter.SelectedIndex=learningStateIndex;}finally{loadingLearning=false;}return;}
        learningKindIndex=LearningPage.Kind.SelectedIndex;learningStateIndex=LearningPage.StateFilter.SelectedIndex;
        await LearningUiAsync(RenderLearningRowsAsync);
    }
    private async Task RefreshLearningPageSafelyAsync()
    {
        try{await RefreshLearningPageAsync(true);}
        catch(OperationCanceledException)when(closing){}
        catch(Exception){if(!closing)LearningPage.SettingsInfo.Text="读取未完成，可点击刷新重试。";}
    }
    private async void LearningSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(loadingLearning)return;
        if(!await CanLeaveLearningEntryAsync())
        {
            loadingLearning=true;try{LearningPage.Rows.SelectedItem=LearningPage.Rows.Items.OfType<LearningRow>().FirstOrDefault(r=>r.Item is LearningEntry entry&&entry.Id==learningEntry?.Id);}finally{loadingLearning=false;}return;
        }
        await LearningUiAsync(ShowLearningSelectionAsync);
    }
    private async Task ShowLearningSelectionAsync()
    {
        var row=LearningPage.Rows.SelectedItem as LearningRow;learningEntry=row?.Item as LearningEntry;CancelLearningEntry();
        LearningPage.Title.Text=row is null?"选择一项查看":row.State;
        LearningPage.Editor.Text=row?.Item switch{LearningEntry e=>e.Text,LearningSummary s=>s.Summary,LearningJob j=>LearningReason(j.Error),_=>""};
        LearningPage.Evidence.Text=row?.Item switch
        {
            LearningEntry e=>$"适用条件：{(e.Condition.Length==0?"此范围内通用":e.Condition)}\n依据：{LearningReason(e.Reason)}\n修订：{e.Revision}",
            LearningSummary s=>$"回执：{s.Sequence} / v{s.Version}\n会话：{s.SessionId}\n回合：{s.TurnId}\n状态：{LearningStateLabel(s.State)}\n"+(s.State=="failed"?"记录超过单次处理容量，未发送；可由Agent提交同一回合的精简修订。\n":"")+string.Join("\n",s.Outputs.Select(o=>o.Name+" · "+o.Purpose+(o.Verified?"（引用已核对）":"（Agent记录，未核对）"))),
            LearningJob j=>$"状态：{LearningStateLabel(j.State)}\n处理方式：{(j.Mode=="chat"?"会话内":"后台")}\n{LearningReason(j.Error)}\n失败或中断后须明确重试。",_=>""
        };
        var selected=learningEntry;
        if(selected is not null&&selected.EvidenceIds.Count>0)
        {
            var evidence=await context.Learning.EvidenceAsync(selected.EvidenceIds,backgroundTasks.Token);
            if(closing||learningEntry?.Id!=selected.Id||editingLearning)return;
            LearningPage.Evidence.Text+="\n\n"+string.Join("\n\n",evidence.Select(v=>$"{v.Evidence.ObservedAt.LocalDateTime:MM-dd HH:mm} · {v.Evidence.TurnId}\n{v.Evidence.Text}"));
        }
        UpdateLearningControls();
    }
    private void UpdateLearningControls()
    {
        if(LearningPage is null)return;var global=LearningScopeId is null;var busy=learningUiBusy;var settingsEditable=editingLearningSettings&&!busy&&!editingLearning;
        LearningPage.Capture.Visibility=LearningPage.Contribute.Visibility=global?Visibility.Collapsed:Visibility.Visible;
        LearningPage.Mode.IsEnabled=LearningPage.Batch.IsEnabled=!global&&settingsEditable;
        LearningPage.Scope.IsEnabled=LearningPage.Kind.IsEnabled=LearningPage.StateFilter.IsEnabled=!busy;
        LearningPage.Refresh.IsEnabled=!busy;
        foreach(var check in new[]{LearningPage.Capture,LearningPage.Learn,LearningPage.Activate,LearningPage.Share,LearningPage.Contribute})check.IsEnabled=settingsEditable;
        LearningPage.Remote.IsEnabled=!global&&settingsEditable&&LearningPage.Mode.SelectedIndex==1;
        LearningPage.SaveSettings.Content=editingLearningSettings?"保存":"修改";
        LearningSettingsCancel.Visibility=editingLearningSettings?Visibility.Visible:Visibility.Collapsed;
        LearningSettingsCancel.IsEnabled=!busy;
        LearningPage.SaveSettings.IsEnabled=!busy&&!loadingLearning&&learningSnapshot is not null&&!editingLearning&&(!ShowingBrowserCache||global);
        LearningPage.Process.Content=LearningPage.Mode.SelectedIndex==1?"立即整合":"复制整合提示词";
        LearningPage.Process.IsEnabled=!busy&&!global&&!editingLearningSettings&&!editingLearning;
        LearningPage.Edit.Content=editingLearning?"保存":"修改";LearningPage.Editor.IsReadOnly=!editingLearning||busy;
        LearningPage.Edit.IsEnabled=!busy&&!editingLearningSettings&&learningEntry is not null;
        LearningPage.Cancel.Visibility=editingLearning?Visibility.Visible:Visibility.Collapsed;LearningPage.Cancel.IsEnabled=!busy;
        LearningPage.Rows.IsEnabled=!busy;
        LearningPage.ToggleState.Content=learningEntry?.State=="active"?"停用":"启用";
        LearningPage.ToggleState.IsEnabled=LearningPage.Undo.IsEnabled=!busy&&!editingLearning&&!editingLearningSettings&&learningEntry is not null;
        LearningPage.Delete.IsEnabled=!busy&&!editingLearning&&!editingLearningSettings&&(learningEntry is not null||LearningPage.Rows.SelectedItem is LearningRow{Item:LearningSummary});
        var retry=LearningPage.Rows.SelectedItem is LearningRow{Item:LearningJob{State:"failed" or "interrupted" or "cancelled"}};
        LearningPage.Retry.Visibility=retry?Visibility.Visible:Visibility.Collapsed;LearningPage.Retry.IsEnabled=!busy&&!editingLearning&&!editingLearningSettings;
    }
    private async Task LearningUiAsync(Func<Task> action)
    {
        if(learningUiBusy||closing)return;learningUiBusy=true;UpdateLearningControls();
        try{await action();}
        catch(OperationCanceledException)when(closing){}
        catch(Exception e){if(!closing){LearningPage.SettingsInfo.Text="操作未完成，请刷新后重试。";await ShowMessageAsync("风格与记忆",e is IOException or InvalidOperationException or ArgumentException?e.Message:"操作未完成。");}}
        finally{learningUiBusy=false;if(!closing)UpdateLearningControls();}
    }
    private async void LearningScopeChanged(object sender,SelectionChangedEventArgs e)
    {
        if(loadingLearning||updating||!ready)return;
        if(!await CanLeaveLearningAsync())
        {loadingLearning=true;try{LearningPage.Scope.SelectedItem=LearningPage.Scope.Items.OfType<ScopeChoice>().FirstOrDefault(s=>s.Id==loadedLearningScope);}finally{loadingLearning=false;}return;}
        await LearningUiAsync(()=>RefreshLearningPageAsync());
    }
    private async Task<bool> CanLeaveLearningEntryAsync()
    {
        if(learningUiBusy)return false;
        if(!editingLearning)return true;
        if(LearningPage.Editor.Text!=learningEntryBefore&&!await ConfirmAsync("放弃修改？","此条目的修改尚未保存。","放弃"))return false;
        CancelLearningEntry();return true;
    }
    private async Task<bool> CanLeaveLearningAsync()
    {
        if(!await CanLeaveLearningEntryAsync())return false;
        if(LearningSettingsDirty&&!await ConfirmAsync("放弃设置修改？","学习设置尚未保存，当前授权不会改变。","放弃"))return false;
        CancelLearningSettings();
        return true;
    }
    private void CancelLearningSettings()
    {
        editingLearningSettings=false;learningDraftRevision++;
        if(learningSnapshot is {Policy:var p})
        {
            loadingLearning=true;
            try
            {
                LearningPage.Capture.IsChecked=p.Capture;LearningPage.Learn.IsChecked=p.Learn;LearningPage.Activate.IsChecked=p.AutoActivate;
                LearningPage.Share.IsChecked=p.Share;LearningPage.Contribute.IsChecked=p.ContributeGlobal;
                LearningPage.Mode.SelectedIndex=p.Mode=="background"?1:0;LearningPage.Batch.Value=p.BatchSize;LearningPage.Remote.IsChecked=p.Remote;
                learningEndpoint=p.Remote&&p.Endpoint.Length>0?p.Endpoint:learningSavedEndpoint;
            }
            finally{loadingLearning=false;}
        }
        UpdateLearningControls();
    }
    private void CancelLearningEntry(){learningDraftRevision++;editingLearning=false;if(learningEntry is not null)LearningPage.Editor.Text=learningEntry.Text;UpdateLearningControls();}
    private async Task SaveLearningSettingsAsync()
    {
        if(learningSnapshot is null||!editingLearningSettings)return;
        if(!LearningSettingsDirty){CancelLearningSettings();return;}
        var scope=LearningScopeId;var next=LearningOptions();var reviewed=learningSnapshot;
        if(scope is not null&&(ShowingBrowserCache||!SourceStore.CodexProjects.Any(p=>p.Id==scope)))throw new InvalidOperationException("项目尚未核对或已经不在本机，请先扫描。");
        var label=SelectedScope(LearningPage.Scope)?.Name??"全局";
        var preview=$"范围：{label}\n记录会话：{(next.Capture?"开启":"关闭")}\n提炼：{(next.Learn?"开启":"关闭")}\n自动启用：{(next.AutoActivate?"开启":"关闭")}\n共享：{(next.Share?"开启":"关闭")}\n"+
            (next.Mode=="background"?$"后台服务：{next.Endpoint}\n仅发送已授权概述及引文，可能产生服务费用。":"会话内由当前Codex整合授权概述，无需额外Key。")+
            (next.ContributeGlobal?"\n本项目参与全局通用偏好提炼；全局学习和共享仍需分别开启。":"");
        if(!await ConfirmAsync("保存学习授权？",preview,"保存"))return;
        var saved=await context.Learning.SetPolicyAsync(scope,next,reviewed.Policy.Revision,backgroundTasks.Token);
        await context.Learning.PublishProjectionAsync(scope,true,backgroundTasks.Token);
        editingLearningSettings=false;learningDraftRevision++;
        learningSnapshot=reviewed with{Policy=saved};
        await RefreshLearningPageAsync();LearningPage.SettingsInfo.Text="已保存；授权前的回合不自动追溯。";
    }
    private async Task EditLearningEntryAsync()
    {
        if(learningEntry is null)return;
        if(!editingLearning){learningDraftRevision++;learningEntryBefore=learningEntry.Text;editingLearning=true;UpdateLearningControls();LearningPage.Editor.Focus(FocusState.Programmatic);return;}
        if(LearningPage.Editor.Text==learningEntryBefore){CancelLearningEntry();return;}
        var reviewed=learningEntry;var text=LearningPage.Editor.Text;
        if(!reviewed.Manual&&!await ConfirmAsync("保存为人工修订？","保存后由你管理，不再由后台覆盖。","保存"))return;
        await context.Learning.EditEntryAsync(reviewed.Id,text,reviewed.Revision,backgroundTasks.Token);editingLearning=false;await RefreshLearningPageAsync();
    }
    private async Task ToggleLearningEntryAsync()
    {
        if(learningEntry is null)return;var state=learningEntry.State=="active"?"disabled":"active";
        if(state=="active"&&!learningEntry.Manual&&!await ConfirmAsync("人工启用此条目？","请核对内容；启用后作为人工条目管理。","启用"))return;
        await context.Learning.SetEntryStateAsync(learningEntry.Id,state,learningEntry.Revision,backgroundTasks.Token);await RefreshLearningPageAsync();
    }
    private async Task UndoLearningEntryAsync(){if(learningEntry is not null){await context.Learning.UndoEntryAsync(learningEntry.Id,learningEntry.Revision,backgroundTasks.Token);await RefreshLearningPageAsync();}}
    private async Task DeleteLearningItemAsync()
    {
        if(!await ConfirmAsync("删除所选内容？",learningEntry is not null?"将停止同类偏好自动恢复，并清除本条修订；不会删除会话概述。":"将删除这条概述并撤回依赖它的自动条目。","删除"))return;
        if(learningEntry is not null)await context.Learning.SetEntryStateAsync(learningEntry.Id,"deleted",learningEntry.Revision,backgroundTasks.Token);
        else if(LearningPage.Rows.SelectedItem is LearningRow{Item:LearningSummary summary})await context.Learning.DeleteSummaryAsync(summary.Sequence,backgroundTasks.Token);
        await RefreshLearningPageAsync();
    }
    private async Task RetryLearningJobAsync(){if(LearningPage.Rows.SelectedItem is LearningRow{Item:LearningJob job}){await context.Learning.RetryAsync(job.Id,backgroundTasks.Token);await RefreshLearningPageAsync();}}
    private async Task ProcessLearningNowAsync()
    {
        var scope=LearningScopeId;if(scope is null)return;
        if((await context.Learning.PolicyAsync(scope,backgroundTasks.Token)).Mode=="chat")
        {CopyText($"请为当前获授权项目 {scope} 查询 Amifolio learning_status；达到整合条件时调用 learning_claim，依据已核对的用户引文提炼并 learning_complete。不要改权限、猜测回合ID或读取其他项目。","整合提示词已复制");return;}
        var saved=await modelConfiguration.LoadAsync(backgroundTasks.Token);
        if(!saved.RemoteAllowed||string.IsNullOrWhiteSpace(saved.ApiKey)||string.IsNullOrWhiteSpace(saved.Model))throw new InvalidOperationException("请先保存AI配置并允许远程发送。");
        _=RunLearningWorkAsync(scope,saved,true);
        LearningPage.SettingsInfo.Text="已在后台排队，可继续其他操作。";
    }
    private static string LearningStatusLabel(string value)=>value switch{"disabled"=>"会话记录已关闭","retry_required"=>"处理未完成","waiting_for_codex"=>"等待Codex整合","waiting_for_idle"=>"等待后台整合","waiting_for_evidence"=>"等待回合核验",_=>"等待新回合"};
    private static string LearningStateLabel(string value)=>value switch{"active"=>"已启用","candidate"=>"观察中","disabled"=>"已停用","verified"=>"已核验","pending_evidence"=>"待核验","no_change"=>"无新增事实","completed"=>"已完成","running"=>"处理中","failed"=>"失败","interrupted"=>"已中断","cancelled"=>"已取消","deleted"=>"已删除",_=>value};
    private static string LearningReason(string value)=>value switch
    {
        ""=>"", "explicit_ongoing_user_preference"=>"用户明确长期要求","explicit_global_user_preference"=>"用户明确全局要求","three_independent_user_turns_and_texts"=>"三份独立用户证据","independent_cross_project_user_evidence"=>"跨项目独立用户证据",
        "agent_report_only"=>"Agent记录，未独立核对完成事实","user_edited"=>"人工修订","user_active"=>"人工启用","user_disabled"=>"用户停用","user_undo"=>"已撤销上次修改",
        "source_withdrawn" or "source_revised" or "record_deleted"=>"来源已失效","participation_withdrawn"=>"项目已退出全局提炼","provider_failed"=>"模型请求未完成","provider_timeout" or "lease_expired"=>"超时或租约过期","invalid_output"=>"返回格式不符",
        "manual_entry_has_priority"=>"人工修订优先","automatic_activation_off"=>"自动启用已关闭","contradictory_evidence" or "conflicting_user_evidence"=>"用户证据存在冲突","task_only"=>"仅适用当前任务",_=>"证据或条件需继续核对"
    };
}
