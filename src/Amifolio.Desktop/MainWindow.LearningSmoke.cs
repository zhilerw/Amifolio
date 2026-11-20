using System.Text.Json;
using Amifolio.Core;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunLearningRegressionAsync(string report)
    {
        var checks=new List<string>();void Check(bool value,string name){if(!value)throw new InvalidOperationException("Learning UI: "+name);checks.Add(name);}
        async Task Idle(){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(learningUiBusy)await Task.Delay(20,deadline.Token);await Task.Delay(100,deadline.Token);}
        var project=await SetupCatalogSmokeAsync("learning-fixture.md",false);
        await NavigateAsync("context");await Idle();await RefreshLearningPageAsync();
        Check(currentPage=="learning"&&LearningPage.ActualWidth>=0,"main-context-route-opens-learning");
        Check(!LearningSettingsDirty&&!editingLearning&&LearningPage.Editor.IsReadOnly,"view-only-default-no-phantom-dirty");
        Check(LearningPage.Capture.Visibility==Visibility.Collapsed,"global-hides-project-capture");
        LearningPage.Scope.SelectedItem=LearningPage.Scope.Items.OfType<ScopeChoice>().Single(s=>s.Id==project.Id);await Idle();
        Check(loadedLearningScope==project.Id&&!LearningSettingsDirty&&smokeDialogAnswers.Count==0,"scope-switch-does-not-prompt-for-untouched-form");
        Check(LearningPage.Capture.IsChecked==false&&LearningPage.Learn.IsChecked==false&&LearningPage.Share.IsChecked==false,"new-consent-defaults-off");
        Check((string)LearningPage.Process.Content=="复制整合提示词","chat-mode-key-free-flow");
        BeginLearningSettingsSmoke();LearningPage.Capture.IsChecked=true;LearningPage.Learn.IsChecked=true;LearningPage.Activate.IsChecked=true;LearningPage.Batch.Value=1;
        Check(LearningSettingsDirty&&LearningPage.SaveSettings.IsEnabled,"explicit-checkbox-change-is-dirty");
        smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);await Idle();
        var policy=await context.Learning.PolicyAsync(project.Id);
        Check(policy.Capture&&policy.Learn&&policy.AutoActivate&&!policy.Share&&!policy.Remote,"authorization-saves-only-selected-permissions");
        Check(!LearningSettingsDirty&&!editingLearningSettings&&!LearningPage.Share.IsEnabled,"saved-form-clean");
        var session=Guid.NewGuid().ToString();var capture=await NativeLearningTurnAsync(project,session,"learn-turn-1","以后请简洁回答");
        var receipt=await context.CaptureLearningAsync(capture,integration.CodexHome);var again=await context.CaptureLearningAsync(capture,integration.CodexHome);
        Check(receipt.State=="verified"&&again.Duplicate&&receipt.Sequence==again.Sequence,"native-user-proof-and-idempotent-receipt");
        var job=(await context.Learning.ClaimAsync(project.Id,"chat"))!;
        Check(job is not null&&await context.Learning.ClaimAsync(project.Id,"chat") is null,"one-owner-per-batch");
        var ev=job!.Evidence.Select(x=>x.Evidence.Id).ToArray();
        await context.CompleteLearningAsync(job.Id,job.Token,[new("style","verbosity","brief","任意模型描述不能成为事实","project","",ev)],integration.CodexHome);
        var snapshot=await context.Learning.SnapshotAsync(project.Id);
        Check(snapshot.Entries.Any(e=>e.Kind=="style"&&e.State=="active"&&e.Text=="偏好简洁回答"),"authorized-automatic-canonical-activation");
        Check((await context.ReadLearningAsync(project.Id,"style","",integration.CodexHome)).Count==0,"learning-does-not-enable-sharing");
        await RefreshLearningPageAsync();BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);await Idle();
        Check((await context.ReadLearningAsync(project.Id,"style","",integration.CodexHome)).Values.Any(s=>s.Contains("偏好简洁回答")),"explicit-sharing-exposes-only-active-layer");
        Check(File.Exists(context.Learning.ProjectionPath(project.Id)),"shared-markdown-projection-created");
        LearningPage.Rows.SelectedItem=LearningPage.Rows.Items.OfType<LearningRow>().Single(r=>r.Item is LearningEntry e&&e.Kind=="style");await Idle();
        Check(learningEntry?.Kind=="style"&&LearningPage.Editor.IsReadOnly&&LearningPage.Evidence.Text.Contains("以后请简洁回答"),"profile-details-show-user-evidence-read-only");
        await LearningUiAsync(EditLearningEntryAsync);LearningPage.Editor.Text="人工修订：回答简洁且具体";
        Check(editingLearning&&!LearningPage.Editor.IsReadOnly&&(string)LearningPage.Edit.Content=="保存","entry-edit-is-explicit");
        var draft=LearningPage.Editor.Text;await RefreshLearningPageAsync(true);
        Check(editingLearning&&LearningPage.Editor.Text==draft,"background-refresh-preserves-profile-draft");
        CancelLearningEntry();Check(!editingLearning&&LearningPage.Editor.Text==learningEntry!.Text,"cancel-restores-before-edit");
        await LearningUiAsync(EditLearningEntryAsync);LearningPage.Editor.Text="人工修订：回答简洁且具体";smokeDialogAnswers.Enqueue(true);await LearningUiAsync(EditLearningEntryAsync);await Idle();
        Check((await context.Learning.SnapshotAsync(project.Id)).Entries.Any(e=>e.Kind=="style"&&e.Manual&&e.Text.Contains("人工修订")),"manual-save-locks-entry");
        await LearningUiAsync(UndoLearningEntryAsync);await Idle();
        Check((await context.Learning.SnapshotAsync(project.Id)).Entries.Any(e=>e.Kind=="style"&&e.Text=="偏好简洁回答"),"undo-restores-prior-entry");
        smokeDialogAnswers.Enqueue(true);await LearningUiAsync(DeleteLearningItemAsync);await Idle();
        Check(!(await context.Learning.SnapshotAsync(project.Id)).Entries.Any(e=>e.Kind=="style"&&e.State=="active"),"delete-withdraws-active-preference");
        LearningPage.Kind.SelectedIndex=1;await Idle();Check(LearningPage.Rows.Items.OfType<LearningRow>().Any(r=>r.Item is LearningSummary),"conversation-log-visible");
        LearningPage.Kind.SelectedIndex=2;await Idle();Check(LearningPage.Rows.Items.OfType<LearningRow>().Any(r=>r.Item is LearningJob{State:"completed"}),"batch-log-visible");
        Check(!LearningSettingsDirty&&await CanLeaveLearningAsync(),"read-only-navigation-remains-clean");
        await context.SetEnabledAsync(project.Id,false);
        Check(!(await context.Learning.PolicyAsync(project.Id)).Share&&!File.Exists(context.Learning.ProjectionPath(project.Id)),"compatibility-withdrawal-disables-canonical-share");
        Root.UpdateLayout();Check(LearningPage.ActualWidth>700&&LearningPage.ActualHeight>300,"native-learning-layout-valid");
        await RenderSnapshotAsync(report+".learning.png");
        return new{checks};
    }
    private void BeginLearningSettingsSmoke()
    {
        if(editingLearningSettings)throw new InvalidOperationException("Settings already editing");
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(LearningPage.SaveSettings).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }
    private async Task<LearningCapture> NativeLearningTurnAsync(CodexProject project,string session,string turn,string text)
    {
        var now=DateTimeOffset.UtcNow.ToString("O");var dir=Path.Combine(integration.CodexHome,"sessions",DateTime.UtcNow.ToString("yyyy"),DateTime.UtcNow.ToString("MM"),DateTime.UtcNow.ToString("dd"));Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"rollout-"+session+".jsonl");
        var lines=new object[]{new{timestamp=now,type="session_meta",payload=new{id=session,cwd=project.RootPath}},new{timestamp=now,type="turn_context",payload=new{turn_id=turn,cwd=project.RootPath}},new{timestamp=now,type="event_msg",payload=new{type="user_message",message=text}}};
        await File.WriteAllLinesAsync(path,lines.Select(x=>JsonSerializer.Serialize(x)));
        return new(project.Id,session,turn,"讨论回答风格，记录用户明确要求。","completed",[text],[]);
    }
}
