using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record LearningHistoryRow(object Item,string Label,string Body,bool IsCold,bool Deleted);
    private Func<ContentDialog,Task<ContentDialogResult>>? learningHistoryProbe;
    private async Task OpenLearningHistoryAsync()
    {
        if(!await CanLeaveLearningAsync())return;
        await LearningUiAsync(OpenLearningHistoryCoreAsync);
    }
    private async Task OpenLearningHistoryCoreAsync()
    {
        var scope=LearningScopeId;
        var kinds=new ComboBox{ItemsSource=new[]{"画像","会话概述"},SelectedIndex=0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var rows=new ListView{DisplayMemberPath=nameof(LearningHistoryRow.Label),Height=170,SelectionMode=ListViewSelectionMode.Single};
        var body=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=230};
        ScrollViewer.SetVerticalScrollBarVisibility(body,ScrollBarVisibility.Auto);
        var info=new TextBlock{Style=(Style)Application.Current.Resources["Caption"],Text="每页50条。恢复后仍沿用原状态和共享设置。"};
        var next=new Button{Content="下一页",IsEnabled=false};var reset=new Button{Content="从头查看"};
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};controls.Children.Add(reset);controls.Children.Add(next);
        var panel=new StackPanel{Width=560,Spacing=10};panel.Children.Add(kinds);panel.Children.Add(rows);panel.Children.Add(body);panel.Children.Add(info);panel.Children.Add(controls);
        var dialog=new ContentDialog{Title="历史记录",Content=panel,PrimaryButtonText="恢复到常用",SecondaryButtonText="删除",CloseButtonText="关闭",IsPrimaryButtonEnabled=false,IsSecondaryButtonEnabled=false,DefaultButton=ContentDialogButton.Close,XamlRoot=Root.XamlRoot,RequestedTheme=ElementTheme.Light,Style=(Style)Application.Current.Resources["AmifolioDialogStyle"]};
        ApplyDialogTheme(dialog);
        string? cursor=null;var loading=false;var open=true;
        async Task Load(bool first)
        {
            if(loading||!open)return;loading=true;kinds.IsEnabled=false;next.IsEnabled=false;reset.IsEnabled=false;
            dialog.IsPrimaryButtonEnabled=false;dialog.IsSecondaryButtonEnabled=false;
            try
            {
                if(first)cursor=null;
                LearningHistoryRow[] items;
                if(kinds.SelectedIndex==1)
                {
                    var page=await context.Learning.ReadReceiptHistoryAsync(scope,cursor,50,backgroundTasks.Token);cursor=page.NextCursor;
                    items=page.Items.Select(i=>new LearningHistoryRow(i,$"{i.Summary.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm} · "+(i.Summary.State=="deleted"?"已删除记录":i.Summary.Summary),i.Summary.Summary,i.IsCold,i.Summary.State=="deleted")).ToArray();
                }
                else
                {
                    var page=await context.Learning.ReadProfileHistoryAsync(scope,cursor,50,backgroundTasks.Token);cursor=page.NextCursor;
                    items=page.Items.Select(i=>new LearningHistoryRow(i,(i.Entry.State=="deleted"?"已删除记录":i.Entry.Text)+" · "+(i.IsCold?"历史":LearningStateLabel(i.Entry.State)),i.Entry.Text,i.IsCold,i.Entry.State=="deleted")).ToArray();
                }
                if(!open)return;rows.ItemsSource=items;body.Text="";next.IsEnabled=cursor is not null;
                info.Text=items.Length==0?"暂无记录。":"每页50条。恢复后仍沿用原状态和共享设置。";
            }
            catch(OperationCanceledException){}
            catch(Exception){if(open)info.Text="读取未完成，请从头重试。";}
            finally{loading=false;if(open){kinds.IsEnabled=true;reset.IsEnabled=true;}}
        }
        rows.SelectionChanged+=(_,_)=>
        {
            var item=rows.SelectedItem as LearningHistoryRow;body.Text=item?.Body??"";
            dialog.IsPrimaryButtonEnabled=!loading&&item is{Deleted:false}&&(item.IsCold||item.Item is LearningProfileHistoryItem);
            dialog.PrimaryButtonText=item is{IsCold:false}?"打开画像":"恢复到常用";dialog.IsSecondaryButtonEnabled=!loading&&item is{Deleted:false};
        };
        kinds.SelectionChanged+=async(_,_)=>await Load(true);next.Click+=async(_,_)=>await Load(false);reset.Click+=async(_,_)=>await Load(true);
        await Load(true);if(closing||currentPage!="learning"||LearningScopeId!=scope)return;
        var result=Option("--smoke-report") is not null&&learningHistoryProbe is not null?await learningHistoryProbe(dialog):await dialog.ShowAsync();open=false;
        if(rows.SelectedItem is not LearningHistoryRow selected||selected.Deleted)return;
        LearningEntry? openedEntry=null;
        if(result==ContentDialogResult.Primary)
        {
            if(selected.Item is LearningReceiptHistoryItem receipt&&selected.IsCold)await context.Learning.RestoreReceiptAsync(receipt.Summary.Sequence,backgroundTasks.Token);
            else if(selected.Item is LearningProfileHistoryItem profile)openedEntry=await context.Learning.RestoreEntryAsync(profile.Entry.Id,backgroundTasks.Token);
            else return;
            LearningPage.SettingsInfo.Text=selected.IsCold?"已恢复，原状态和共享设置保留。":"已打开画像。";
        }
        else if(result==ContentDialogResult.Secondary)
        {
            if(!await ConfirmAsync("删除记录","删除正文并保留防重放记录，已删除内容不能恢复。","删除"))return;
            if(selected.Item is LearningReceiptHistoryItem receipt)await context.Learning.DeleteSummaryAsync(receipt.Summary.Sequence,backgroundTasks.Token);
            else if(selected.Item is LearningProfileHistoryItem profile)await context.Learning.SetEntryStateAsync(profile.Entry.Id,"deleted",profile.Entry.Revision,backgroundTasks.Token);
        }
        else return;
        await RefreshLearningPageAsync(true);
        if(openedEntry is not null&&learningSnapshot is not null&&!closing&&currentPage=="learning"&&LearningScopeId==scope)
        {
            // Explicit history selection must remain editable outside the bounded view.
            learningSnapshot=learningSnapshot with{Entries=[openedEntry,..learningSnapshot.Entries.Where(e=>e.Id!=openedEntry.Id)]};
            learningEntry=openedEntry;loadingLearning=true;
            try{LearningPage.Kind.SelectedIndex=0;LearningPage.StateFilter.SelectedIndex=0;learningKindIndex=0;learningStateIndex=0;}
            finally{loadingLearning=false;}
            await RenderLearningRowsAsync();
        }
    }
}
