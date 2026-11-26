using System.Net;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunStage9RegressionAsync(string report)
    {
        var checks=new List<string>();
        var progressDispatcher=DispatcherQueue;var progressGate=new object();
        void Progress(string stage,Exception? error=null)
        {
            var line=$"{DateTimeOffset.UtcNow:O} thread={Environment.CurrentManagedThreadId} ui={progressDispatcher.HasThreadAccess} page={currentPage} checks={checks.Count} {stage}"+(error is null?"":"\n"+error)+"\n";
            lock(progressGate)
            {
                using var stream=new FileStream(report+".progress",FileMode.Append,FileAccess.Write,FileShare.ReadWrite,4096,FileOptions.WriteThrough);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(line));stream.Flush(true);
            }
        }
        void Check(bool value,string name)
        {
            Progress("check:"+(value?"pass:":"failed:")+name);
            if(!value)throw new InvalidOperationException("Settings UI: "+name);checks.Add(name);
        }
        async Task<T> AwaitValue<T>(string stage,Func<Task<T>> action)
        {
            Progress(stage+":before");
            var value=await action();Progress(stage+":after");return value;
        }
        async Task AwaitStep(string stage,Func<Task> action)
        {
            Progress(stage+":before");await action();Progress(stage+":after");
        }
        void Invoke(Button button)
        {
            Progress("invoke:"+button.Name+":peer-constructor:before");
            var peer=new ButtonAutomationPeer(button);
            Progress("invoke:"+button.Name+":peer-constructor:after");
            var pattern=peer.GetPattern(PatternInterface.Invoke);
            Progress("invoke:"+button.Name+":get-pattern:after");
            var provider=(IInvokeProvider)pattern;
            Progress("invoke:"+button.Name+":provider-invoke:before");
            provider.Invoke();Progress("invoke:"+button.Name+":provider-invoke:returned");
        }
        async Task Wait(Func<bool> condition,string stage)
        {
            Progress(stage+":wait:before");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!condition())await Task.Delay(10,timeout.Token);Progress(stage+":wait:after");
        }
        void XamlUnhandled(object sender,Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {try{Progress("xaml-unhandled (not marked handled)",args.Exception);}catch{}}
        void DomainUnhandled(object sender,System.UnhandledExceptionEventArgs args)
        {try{Progress("domain-unhandled terminating="+args.IsTerminating,args.ExceptionObject as Exception);}catch{}}
        Progress("settings:enter");
        Application.Current.UnhandledException+=XamlUnhandled;
        AppDomain.CurrentDomain.UnhandledException+=DomainUnhandled;
        try
        {
            await AwaitValue("navigate:settings",()=>RequestNavigationAsync("settings"));await AwaitStep("retire-legacy",RetireLegacyInBackgroundAsync);Progress("layout:initial:before");Root.UpdateLayout();Progress("layout:initial:after");
            Check(currentPage=="settings"&&SettingsNav.Style==(Style)Application.Current.Resources["SelectedNavButton"],"settings-route-and-sidebar-selection");
            Check(HistoryButton.Visibility==Visibility.Visible&&CheckUpdatesButton.Visibility==Visibility.Visible&&ReleasePageButton.Visibility==Visibility.Visible,"history-check-and-release-controls-visible");
            Check(context.LegacyRetired&&StorageStatusText.Text.Contains("只读历史"),"storage-retirement-status-visible");
            Check(ReleaseStatusText.Text.Contains("私有")&&releasePage==GitHubReleaseService.RepoReleasesPage,"initial-update-status-does-not-claim-latest");
            var scroll=SettingsPage.Children.OfType<ScrollViewer>().Single();
            var heading=SettingsPage.Children.OfType<TextBlock>().Single();
            var scrollContent=(FrameworkElement)scroll.Content;
            object ScrollLayout()
            {
                var headingRoot=heading.TransformToVisual(Root).TransformPoint(new());
                var headingPage=heading.TransformToVisual(SettingsPage).TransformPoint(new());
                var pageRoot=SettingsPage.TransformToVisual(Root).TransformPoint(new());
                var bodyPage=scroll.TransformToVisual(SettingsPage).TransformPoint(new());
                var contentScroll=scrollContent.TransformToVisual(scroll).TransformPoint(new());
                return new{headingRoot=new{x=headingRoot.X,y=headingRoot.Y},headingPage=new{x=headingPage.X,y=headingPage.Y},pageRoot=new{x=pageRoot.X,y=pageRoot.Y},bodyPage=new{x=bodyPage.X,y=bodyPage.Y},contentScroll=new{x=contentScroll.X,y=contentScroll.Y},headingRow=Grid.GetRow(heading),scrollRow=Grid.GetRow(scroll),rows=SettingsPage.RowDefinitions.Select(r=>new{unit=r.Height.GridUnitType.ToString(),height=r.ActualHeight}).ToArray(),pageWidth=SettingsPage.ActualWidth,pageHeight=SettingsPage.ActualHeight,scrollWidth=scroll.ActualWidth,scrollHeight=scroll.ActualHeight,scroll.ViewportHeight,scroll.ExtentHeight,scroll.ScrollableHeight,scroll.VerticalOffset,activeAnimations=motion?.ActiveCount};
            }
            Progress("scroll:before-motion-settle "+JsonSerializer.Serialize(ScrollLayout()));
            await Wait(()=>motion is null||motion.ActiveCount==0,"settings:initial-motion-finished");Root.UpdateLayout();
            var originalMaxHeight=scroll.MaxHeight;var originalOffset=scroll.VerticalOffset;
            try
            {
                // Exercise a real scrolling body even when the full-size window fits all settings.
                if(scroll.ScrollableHeight<=0){scroll.MaxHeight=Math.Min(originalMaxHeight,160);Root.UpdateLayout();await AwaitStep("scroll:viewport-settle",()=>Task.Delay(80));Root.UpdateLayout();}
                var headingPageBefore=heading.TransformToVisual(SettingsPage).TransformPoint(new());
                var headingRootBefore=heading.TransformToVisual(Root).TransformPoint(new());
                var contentBefore=scrollContent.TransformToVisual(scroll).TransformPoint(new());
                var offsetBefore=scroll.VerticalOffset;
                var targetOffset=Math.Min(1000,scroll.ScrollableHeight);
                Progress("scroll:layout-before "+JsonSerializer.Serialize(ScrollLayout()));
                var changeAccepted=scroll.ChangeView(null,targetOffset,null,true);
                Progress("scroll:change-view accepted="+changeAccepted+" target="+targetOffset);
                await Wait(()=>scroll.VerticalOffset>offsetBefore,"scroll:offset-changed");
                await AwaitStep("scroll:settle",()=>Task.Delay(80));Root.UpdateLayout();
                var headingPageAfter=heading.TransformToVisual(SettingsPage).TransformPoint(new());
                var headingRootAfter=heading.TransformToVisual(Root).TransformPoint(new());
                var contentAfter=scrollContent.TransformToVisual(scroll).TransformPoint(new());
                Progress("scroll:layout-after "+JsonSerializer.Serialize(ScrollLayout()));
                var fixedRows=SettingsPage.RowDefinitions.Count==2&&SettingsPage.RowDefinitions[0].Height.IsAuto&&SettingsPage.RowDefinitions[1].Height.IsStar&&Grid.GetRow(heading)==0&&Grid.GetRow(scroll)==1;
                Check(fixedRows&&headingPageAfter.X==headingPageBefore.X&&headingPageAfter.Y==headingPageBefore.Y&&headingRootAfter.X==headingRootBefore.X&&headingRootAfter.Y==headingRootBefore.Y&&scroll.VerticalOffset>offsetBefore&&contentAfter.Y<contentBefore.Y,"settings-heading-stays-fixed-on-scroll");
            }
            finally{scroll.MaxHeight=originalMaxHeight;scroll.ChangeView(null,originalOffset,null,true);Root.UpdateLayout();Progress("scroll:layout-restored "+JsonSerializer.Serialize(ScrollLayout()));}
            var policy=await AwaitValue("learning:policy",()=>context.Learning.PolicyAsync(null));
            var historyRoot=Path.Combine(dataRoot,"history","legacy-"+Guid.NewGuid().ToString("N"));
            var state=Path.Combine(historyRoot,"context-state","global","state.json");var prefs=Path.Combine(historyRoot,"preference-state","state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);Directory.CreateDirectory(Path.GetDirectoryName(prefs)!);
            await AwaitStep("history:write-state",()=>File.WriteAllTextAsync(state,JsonSerializer.Serialize(new{Documents=new Dictionary<string,string>{{"style.md","historic-style-synthetic"}},Candidates=new Dictionary<string,string>{{"memory.md","historic-candidate-synthetic"}}})));
            await AwaitStep("history:write-preferences",()=>File.WriteAllTextAsync(prefs,JsonSerializer.Serialize(new{Entries=new[]{new{Text="historic-preference-synthetic"}}})));
            var stateBefore=await AwaitValue("history:read-state-bytes",()=>File.ReadAllBytesAsync(state));var prefsBefore=await AwaitValue("history:read-preferences-bytes",()=>File.ReadAllBytesAsync(prefs));var modified=File.GetLastWriteTimeUtc(state);
            var documents=await AwaitValue("history:load",LoadLegacyHistoryAsync);
            Check(documents.Count==3&&documents.Any(d=>d.Text=="historic-preference-synthetic"),"history-loader-includes-documents-candidates-and-preferences");
            Check(HistoryTitle("Documents / style.md")=="风格"&&HistoryTitle("Candidates / memory.md")=="候选 · 记忆","history-row-labels-distinguish-formal-and-candidate");
            Check(documents.All(d=>d.Scope=="global"&&d.Archive==Path.GetFileName(historyRoot)),"history-rows-retain-scope-and-archive-identity");
            Check(documents.Single(d=>d.Name=="Documents / style.md").Text=="historic-style-synthetic"&&documents.Single(d=>d.Name=="Candidates / memory.md").Text=="historic-candidate-synthetic","history-loader-keeps-formal-and-candidate-content-separate");
            Check((await AwaitValue("history:load",LoadLegacyHistoryAsync)).SequenceEqual(documents),"repeated-history-load-is-idempotent");
            Check(HistoryButton.FontFamily.Source=="Segoe UI"&&AgentsEditor.FontFamily.Source=="Segoe UI"&&ModelKey.FontFamily.Source=="Segoe UI"&&((Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"]).Source=="Segoe UI"&&HistoryButton.CornerRadius.TopLeft==5&&(CornerRadius)Application.Current.Resources["OverlayCornerRadius"]==new CornerRadius(5),"global-text-uses-shell-font-and-small-corners");
            Check((await AwaitValue("history:read-state-bytes",()=>File.ReadAllBytesAsync(state))).SequenceEqual(stateBefore)&&(await AwaitValue("history:read-preferences-bytes",()=>File.ReadAllBytesAsync(prefs))).SequenceEqual(prefsBefore)&&File.GetLastWriteTimeUtc(state)==modified,"history-view-preserves-source-bytes-and-time");
            Check((await AwaitValue("learning:policy",()=>context.Learning.PolicyAsync(null)))==policy&&(await AwaitValue("learning:snapshot",()=>context.Learning.SnapshotAsync(null))).Entries.Count==0,"history-view-does-not-enable-learning-or-import-entries");
            smokeMessages.Enqueue("旧资料");Progress("history:actual-panel:open-before");
            var historyShowing=OpenLegacyHistoryAsync();
            var historyPanelRendered=false;var historyPreviewReadOnly=false;var historySelectionCorrect=false;
            try
            {
            using(var historyDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while(!historyShowing.IsCompleted&&!historyPanelRendered)
                {
                    Root.UpdateLayout();
                    var historyDialog=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Where(popup=>popup.IsOpen&&popup.Child is not null).SelectMany(popup=>SmokeVisuals(popup.Child)).OfType<ContentDialog>().FirstOrDefault(dialog=>dialog.Title as string=="旧资料");
                    if(historyDialog?.Content is StackPanel historyPanel)
                    {
                        var historyPreview=historyPanel.Children.OfType<TextBox>().Single();
                        var historyList=historyPanel.Children.OfType<ListView>().Single();
                        if(historyPreview.ActualWidth>0&&historyPreview.ActualHeight>0)
                        {
                            historyPreviewReadOnly=historyPreview.IsReadOnly;
                            historySelectionCorrect=historyList.Items.Count==documents.Count&&historyPreview.Text==documents[0].Text;
                            Progress("history:actual-panel:layout "+JsonSerializer.Serialize(new{width=historyPanel.ActualWidth,height=historyPanel.ActualHeight,previewWidth=historyPreview.ActualWidth,previewHeight=historyPreview.ActualHeight,readOnly=historyPreviewReadOnly,rows=historyList.Items.Count,captionResourcePresent=Application.Current.Resources.ContainsKey("Caption")}));
                            var historyBitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                            Progress("history:actual-panel:render-before");
                            await historyBitmap.RenderAsync(historyPanel);
                            var historyPixels=await historyBitmap.GetPixelsAsync();
                            historyPanelRendered=historyBitmap.PixelWidth>0&&historyBitmap.PixelHeight>0&&historyPixels.Length>0;
                            Progress("history:actual-panel:render-after pixels="+historyPixels.Length);
                        }
                    }
                    if(!historyPanelRendered)await Task.Delay(10,historyDeadline.Token);
                }
            }
            }
            finally{await AwaitStep("history:actual-panel:closed",()=>historyShowing);}
            var historyBytesPreserved=(await AwaitValue("history:read-state-after-panel",()=>File.ReadAllBytesAsync(state))).SequenceEqual(stateBefore)&&(await AwaitValue("history:read-preferences-after-panel",()=>File.ReadAllBytesAsync(prefs))).SequenceEqual(prefsBefore)&&File.GetLastWriteTimeUtc(state)==modified;
            Check(historyPanelRendered&&historyPreviewReadOnly&&historySelectionCorrect&&historyBytesPreserved&&HistoryButton.IsEnabled&&smokeMessages.Count==0,"history-actual-panel-renders-read-only-and-closes");
            var calls=0;var gate=new TaskCompletionSource<ReleaseCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            smokeReleaseCheck=()=>{Progress("update:gated-handler:entered");calls++;return gate.Task;};
            try
            {
                Check(calls==0&&CheckUpdatesButton.IsEnabled,"update-check-waits-for-explicit-click");
                Invoke(CheckUpdatesButton);await Wait(()=>calls==1,"update:handler-entered");
                Check(!CheckUpdatesButton.IsEnabled&&ReleaseStatusText.Text=="正在检查"&&HistoryButton.IsEnabled,"pending-update-disables-only-check-action");
                await AwaitStep("update:check-release",CheckReleaseAsync);
                Check(calls==1,"repeated-update-checks-do-not-overlap");
                Check(await AwaitValue("navigate:library",()=>RequestNavigationAsync("library"))&&await AwaitValue("navigate:settings",()=>RequestNavigationAsync("settings"))&&!gate.Task.IsCompleted,"navigation-works-during-update-check");
                using var handler=new Stage9ReleaseHandler();using var http=new HttpClient(handler);var releases=new GitHubReleaseService(http);
                handler.Status=HttpStatusCode.NotFound;gate.SetResult(await AwaitValue("update:synthetic-service",()=>releases.CheckAsync("0.1.3")));await Wait(()=>CheckUpdatesButton.IsEnabled,"update:check-enabled");
                Check(ReleaseStatusText.Text.Contains("无法确认")&&ReleaseStatusText.Text.Contains("私有")&&releasePage==GitHubReleaseService.RepoReleasesPage,"private-release-does-not-report-up-to-date");
                handler.Status=HttpStatusCode.OK;smokeReleaseCheck=()=>releases.CheckAsync("0.1.3");await AwaitStep("update:check-release",CheckReleaseAsync);
                Check(ReleaseStatusText.Text.Contains("新稳定版本")&&releasePage.AbsoluteUri=="https://github.com/zhilerw/Amifolio/releases/tag/v9.0.0","update-uses-trusted-release-url-ignoring-response-links");
                Check(ReleasePageButton.IsEnabled&&string.IsNullOrEmpty(ModelKey.Password),"download-page-remains-available-without-model-key");
                handler.Offline=true;await AwaitStep("update:check-release",CheckReleaseAsync);
                Check(CheckUpdatesButton.IsEnabled&&ReleaseStatusText.Text.Contains("网络不可用")&&releasePage==GitHubReleaseService.RepoReleasesPage,"offline-check-restores-action-and-safe-page");
                smokeReleaseCheck=()=>Task.FromException<ReleaseCheckResult>(new OperationCanceledException());await AwaitStep("update:check-release",CheckReleaseAsync);
                Check(CheckUpdatesButton.IsEnabled&&ReleaseStatusText.Text.Contains("停止"),"cancelled-check-can-be-retried");
                Check(!Directory.EnumerateFiles(dataRoot,"*",SearchOption.AllDirectories).Any(p=>Path.GetExtension(p) is ".exe" or ".zip"),"update-check-does-not-download-installers");
                Check(smokeMessages.Count==0&&smokeDialogAnswers.Count==0,"settings-checks-leave-no-pending-dialog-answers");
                await AwaitStep("snapshot:settings",()=>RenderSnapshotAsync(report+".settings-regression.png"));
                Progress("settings:checks-and-snapshot-complete");
                return new{checks,providerRequests=handler.Requests,syntheticOnly=true,externalBrowserOpened=false,historyDialogOpened=true};
            }
            finally{gate.TrySetCanceled();smokeReleaseCheck=null;}
        }
        catch(Exception ex){Progress("settings:managed-exception",ex);throw;}
        finally
        {
            Application.Current.UnhandledException-=XamlUnhandled;
            AppDomain.CurrentDomain.UnhandledException-=DomainUnhandled;
            Progress("settings:leave");
        }
    }
    private sealed class Stage9ReleaseHandler:HttpMessageHandler
    {
        internal HttpStatusCode Status=HttpStatusCode.OK;
        internal bool Offline;
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Requests++;
            if(Offline)throw new HttpRequestException("synthetic offline");
            return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(JsonSerializer.Serialize(new{tag_name="v9.0.0",draft=false,prerelease=false,html_url="https://untrusted.invalid/download",browser_download_url="https://untrusted.invalid/installer.exe"}))});
        }
    }
}
