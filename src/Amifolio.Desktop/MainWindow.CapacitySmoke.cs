using Amifolio.Core;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunCapacityRegressionAsync()
    {
        var checks=new List<string>();void Check(bool value,string name){if(!value)throw new InvalidOperationException("Capacity UI: "+name);checks.Add(name);}
        async Task ClickHistory()
        {
            var original=learningHistoryProbe??throw new InvalidOperationException("History probe required.");var shown=false;
            learningHistoryProbe=async dialog=>{shown=true;return await original(dialog);};
            try
            {
                var peer=Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(LearningPage.History);
                var invoke=(Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                invoke.Invoke();var watch=System.Diagnostics.Stopwatch.StartNew();
                while((!shown||learningUiBusy)&&watch.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(20);
                if(!shown||learningUiBusy)throw new InvalidOperationException("History button did not complete its real command route.");
            }
            finally{learningHistoryProbe=original;}
        }
        var project=await SetupCatalogSmokeAsync("capacity-fixture.md",false);
        await OpenLearningPageAsync(false,project.Id,true);await RefreshLearningPageAsync();
        Check(LearningPage.Capacity.Text.Contains("全库常用概述")&&LearningPage.History.IsEnabled,"capacity-and-history-visible");
        var id=new string('e',64);var entry=new LearningEntry(id,project.Id,"style","response.spacing","compact","synthetic archived preference","","disabled","user_disabled",false,1,DateTimeOffset.UtcNow.AddDays(-60),[]);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(dataRoot,"learning","state.sqlite"),Pooling=false}.ToString()))
        {
            db.Open();using var command=db.CreateCommand();command.CommandText="INSERT INTO profiles(id,scope,updated,payload) VALUES($id,$scope,$updated,$payload)";
            command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$scope",project.Id);command.Parameters.AddWithValue("$updated",entry.UpdatedAt.ToUnixTimeMilliseconds());command.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(entry,new JsonSerializerOptions(JsonSerializerDefaults.Web)));command.ExecuteNonQuery();
        }
        await context.Learning.ArchiveColdAsync(10);var capacity=await context.Learning.CapacityAsync();
        Check(capacity.ColdProfiles==1&&capacity.HotProfiles==0,"cold-entry-preserved-outside-hot-limit");
        var history=await context.Learning.ReadProfileHistoryAsync(project.Id);
        Check(history.Items.Single().IsCold&&history.Items[0].Entry.Text==entry.Text,"cold-body-readable");
        Check(!(await context.Learning.PolicyAsync(project.Id)).Share,"history-does-not-grant-sharing");
        learningHistoryProbe=async dialog=>
        {
            var showing=dialog.ShowAsync();await Task.Delay(150);Root.UpdateLayout();
            var panel=(StackPanel)dialog.Content;var list=(ListView)panel.Children[1];var text=(TextBox)panel.Children[2];
            Check(text.IsReadOnly&&list.Items.Count==1,"history-panel-is-read-only-and-bounded");
            list.SelectedIndex=0;await Task.Delay(20);
            Check(text.Text==entry.Text&&dialog.IsPrimaryButtonEnabled&&dialog.IsSecondaryButtonEnabled,"cold-selection-can-restore-or-delete");
            Check(dialog.Style==(Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["AmifolioDialogStyle"],"history-dialog-theme");
            dialog.Hide();await showing;return ContentDialogResult.Primary;
        };
        try
        {
            await ClickHistory();capacity=await context.Learning.CapacityAsync();
            Check(capacity.ColdProfiles==0&&capacity.HotProfiles==1,"restore-ui-moves-body-to-hot");
            Check(learningEntry?.Id==id&&LearningPage.Editor.Text==entry.Text&&LearningPage.Edit.IsEnabled,"restored-history-entry-opens-in-editable-detail");
            Check((await context.Learning.SnapshotAsync(project.Id)).Entries.Single().State=="disabled"&&!(await context.Learning.PolicyAsync(project.Id)).Share,"restore-preserves-state-and-permission");
            await context.Learning.ArchiveColdAsync(10);
            learningHistoryProbe=async dialog=>
            {
                var showing=dialog.ShowAsync();await Task.Delay(120);((ListView)((StackPanel)dialog.Content).Children[1]).SelectedIndex=0;
                dialog.Hide();await showing;return ContentDialogResult.Secondary;
            };
            smokeDialogAnswers.Enqueue(true);await ClickHistory();
            capacity=await context.Learning.CapacityAsync();Check(capacity.ColdProfiles==0&&capacity.DeletedProfiles==1,"delete-cold-ui-clears-body-keeps-protection");
            Check((await context.Learning.ReadProfileHistoryAsync(project.Id)).Items.Single().Entry.Text=="","deleted-history-never-reveals-text");
            Check(smokeDialogAnswers.Count==0&&!editingLearning&&!LearningSettingsDirty,"history-actions-leave-clean-edit-state");
            return new{checks,syntheticOnly=true};
        }
        finally{learningHistoryProbe=null;}
    }
}
