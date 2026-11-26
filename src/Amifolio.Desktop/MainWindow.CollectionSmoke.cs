using Amifolio.Context;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private string browserSmokeKeyTrace="";
    private void TraceBrowserSmokeKey(KeyRoutedEventArgs e,DependencyObject? focused)
    {
        if(Option("--smoke-mode")!="collection")return;
        browserSmokeKeyTrace+=$"[{e.Key},alt={Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)},ctrl={Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)},shift={Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)},focus={focused?.GetType().Name}:{(focused as FrameworkElement)?.Name},selected={ArtifactList.SelectedItems.Count},handled={e.Handled},route={CanRouteBrowserInput(focused,true)},busy={operation is not null}]";
        if(browserSmokeKeyTrace.Length>3000)browserSmokeKeyTrace=browserSmokeKeyTrace[^3000..];
    }
    private readonly Queue<string> smokeMessages=new();
    private string? smokeSourceRoot;
    private async Task<CodexProject> SetupCatalogSmokeAsync(string fileName="notes.md",bool nested=true)
    {
        var source=smokeSourceRoot??=Path.Combine(Path.GetTempPath(),"Amifolio-native-reference-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source,fileName),"用户明确要求：简洁、准确，米白与松石绿。needle synthetic");
        if(nested){Directory.CreateDirectory(Path.Combine(source,"reports"));await File.WriteAllTextAsync(Path.Combine(source,"reports","nested.md"),"nested source");}
        Directory.CreateDirectory(integration.CodexHome);
        await File.WriteAllTextAsync(Path.Combine(integration.CodexHome,".codex-global-state.json"),JsonSerializer.Serialize(new Dictionary<string,object>{{"local-projects",new Dictionary<string,object>{{"p",new{id="p",name="只读项目",rootPaths=new[]{source}}}}}}));
        await SourceStore.RefreshCodexProjectsAsync();await RefreshProjectsAsync();return SourceStore.CodexProjects.Single();
    }
    private async Task<object> RunCollectionRegressionAsync()
    {
        var checks=new List<string>();void Check(bool value,string name){if(!value)throw new InvalidOperationException("Reference UI regression: "+name);checks.Add(name);}
        // The isolated parser has a 30-second deadline; the test must wait past its result.
        async Task Idle(){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));while(operation is not null||parsingFiles)await Task.Delay(20,timeout.Token);await Task.Delay(50,timeout.Token);}
        await NavigateAsync("connections");
        Check(integration.ConfigPath.StartsWith(dataRoot,StringComparison.OrdinalIgnoreCase),"host-configuration-isolated");
        Check(AgentTarget.Items.Count==1&&AgentTarget.SelectedItem as string=="Codex","codex-only-selector");
        Check(CodexConfigPath.Text==integration.ConfigPath&&CodexSkillPath.Text==integration.SkillDirectory,"exact-setup-paths-visible");
        Directory.CreateDirectory(integration.CodexHome);await File.WriteAllTextAsync(integration.ConfigPath,"# user configuration\nmodel = 'synthetic-model'\n");
        smokeDialogAnswers.Enqueue(true);smokeMessages.Enqueue("配置已写入");InstallMcp(this,new RoutedEventArgs());await Idle();var config=await File.ReadAllTextAsync(integration.ConfigPath);
        Check(config.StartsWith("# user configuration")&&config.Contains("[mcp_servers.amifolio]"),"mcp-appends-without-overwrite");
        smokeDialogAnswers.Enqueue(true);smokeMessages.Enqueue("配置已写入");InstallMcp(this,new RoutedEventArgs());await Idle();Check(config==await File.ReadAllTextAsync(integration.ConfigPath),"mcp-repeat-idempotent");
        smokeDialogAnswers.Enqueue(true);InstallSkill(this,new RoutedEventArgs());await Idle();
        Check(string.IsNullOrEmpty((await modelConfiguration.LoadAsync()).ApiKey)&&await File.ReadAllTextAsync(Path.Combine(integration.SkillDirectory,"SKILL.md"))==await BundledSkillAsync(CancellationToken.None),"trusted-skill-exact-destination-without-key");
        Check(integration.SkillArchive(await BundledSkillAsync(CancellationToken.None)).Length>0,"skill-package-available");
        var globalPolicy=await context.Learning.PolicyAsync(null);await context.Learning.SetPolicyAsync(null,globalPolicy with{Share=true},globalPolicy.Revision);
        var global=Path.Combine(integration.CodexHome,"AGENTS.md");await File.WriteAllTextAsync(global,"# User rules\nKeep these.\n");
        smokeDialogAnswers.Enqueue(true);DeployAgentsEntry(this,new RoutedEventArgs());await Idle();var globalText=await File.ReadAllTextAsync(global);
        Check(globalText.StartsWith("# User rules\nKeep these.\n")&&globalText.Contains("---\n<!-- folio:begin"),"global-entry-appends-with-separators");
        smokeDialogAnswers.Enqueue(true);DeployAgentsEntry(this,new RoutedEventArgs());await Idle();Check(globalText==await File.ReadAllTextAsync(global),"entry-repeat-idempotent");
        GoToAgents(this,new RoutedEventArgs());await Idle();Check(currentPage=="agents"&&AgentsEditor.IsReadOnly,"entry-jump-read-only");
        var project=await SetupCatalogSmokeAsync();var source=Path.Combine(project.RootPath,"notes.md");var before=await File.ReadAllTextAsync(source);var time=File.GetLastWriteTimeUtc(source);
        Check(projects.Count==1&&ProjectsList.Items.Count==1,"projects-from-real-codex-registry");
        await NavigateAsync("library");await ShowBrowserAsync(null);
        Check(ArtifactList.Items.Count==1&&ArtifactList.Items.OfType<ArtifactItem>().Single().IsDirectory,"all-projects-default-root");Root.UpdateLayout();
        Check(ArtifactList.ItemsPanelRoot is ItemsWrapGrid,"medium-icon-virtualized-grid");
        Check(LibraryHeading.Text=="作品"&&Root.FindName("ProjectFilter") is null,"work-title-and-no-project-dropdown");
        Check(Root.FindName("ImportButton") is null&&Root.FindName("EditTitle") is null&&Root.FindName("EditProject") is null,"no-upload-or-metadata-edit-controls");
        await ShowBrowserAsync(project);Check(ArtifactList.Items.Count==2&&ArtifactList.Items.OfType<ArtifactItem>().Count(x=>x.IsDirectory)==1,"immediate-hierarchy-not-flattened");
        Check((await store.ListAsync()).Count==0,"structure-scan-does-not-parse-or-index-content");
        await ShowBrowserAsync(project,Path.Combine(project.RootPath,"reports"));Check(ArtifactList.Items.OfType<ArtifactItem>().Single().Title=="nested.md","nested-original-directory");
        Check(((IEnumerable<BreadcrumbItem>)Breadcrumbs.ItemsSource).Count()==3,"hierarchical-breadcrumbs");
        await ShowBrowserAsync(project);ArtifactList.SelectedItem=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>!x.IsDirectory);
        Check(DetailPreview.Text.Contains("点击解析"),"unparsed-file-asks-for-explicit-parse");
        ParseSelectedFiles(this,new RoutedEventArgs());await Idle();var parsed=(await store.ListAsync()).Single();
        Check(parsed.IsReference&&parsed.ObjectPath==source,"parse-keeps-original-path");
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(DetailPreview.Text=="正在核对来源…")await Task.Delay(20,timeout.Token);
        Check(parsed.Preview.Contains("[行 1]")&&DetailPreview.Text.Contains("needle"),"read-only-parser-and-line-locator");
        Check(!Directory.Exists(Path.Combine(dataRoot,"objects"))||!Directory.EnumerateFiles(Path.Combine(dataRoot,"objects")).Any(),"no-original-copy-created");
        Check(await File.ReadAllTextAsync(source)==before&&File.GetLastWriteTimeUtc(source)==time,"source-bytes-and-time-unchanged");
        Check(await OpenLibraryContextAsync()&&LearningScopeId==project.Id&&currentPage=="learning","memory-entry-preserves-browser-project");
        Check(LearningPage.Back.Visibility==Visibility.Collapsed&&ContextNav.Style==(Style)Application.Current.Resources["SelectedNavButton"],"main-learning-hides-back-and-highlights-context");
        Root.UpdateLayout();var learningHeading=SmokeVisuals(LearningPage).OfType<TextBlock>().Single(t=>t.Text=="风格与记忆");var headingY=learningHeading.TransformToVisual(Root).TransformPoint(new()).Y;
        var settingsScroll=SmokeVisuals(LearningPage).OfType<ScrollViewer>().First();settingsScroll.ChangeView(null,1000,null,true);Root.UpdateLayout();
        Check(learningHeading.TransformToVisual(Root).TransformPoint(new()).Y==headingY,"learning-heading-pinned-outside-settings-scroll");
        settingsScroll.ChangeView(null,0,null,true);
        Check(LearningPage.Editor.IsReadOnly&&!LearningPage.Capture.IsEnabled,"learning-opens-in-view-mode");
        BeginLearningSettingsSmoke();LearningPage.Capture.IsChecked=true;LearningPage.Learn.IsChecked=true;
        Check(LearningSettingsDirty&&LearningSettingsCancel.Visibility==Visibility.Visible,"learning-explicit-modify-shows-cancel");
        CancelLearningSettings();Check(!LearningSettingsDirty&&LearningPage.Capture.IsChecked==false,"learning-cancel-restores-consent");
        var projectPolicy=await context.Learning.PolicyAsync(project.Id);
        Check(!projectPolicy.Share&&!projectPolicy.Capture,"opening-learning-does-not-grant-permission");
        BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        Check((await context.Learning.PolicyAsync(project.Id)).Share,"project-learning-sharing-explicitly-saved");
        Check(context.LearningEntryDirectory(project.Id)==Path.GetDirectoryName(context.Learning.ProjectionPath(project.Id)),"project-entry-uses-canonical-learning-directory");
        await NavigateAsync("connections");updating=true;IntegrationScope.SelectedItem=IntegrationScope.Items.OfType<ScopeChoice>().Single(p=>p.Id==project.Id);updating=false;
        smokeDialogAnswers.Enqueue(true);DeployAgentsEntry(this,new RoutedEventArgs());await Idle();Check(File.Exists(Path.Combine(project.RootPath,"AGENTS.md")),"explicit-config-exception-project-entry");
        Check(await File.ReadAllTextAsync(global)==globalText,"project-entry-does-not-touch-global");
        smokeDialogAnswers.Enqueue(true);InstallCollectionHook(this,new RoutedEventArgs());await Idle();Check(File.Exists(integration.HooksPath),"metadata-hook-installs-isolated");
        Check(!CollectionGrantStatus.Text.Contains("授权输出"),"old-output-copy-grants-removed");
        Root.UpdateLayout();var body=ConnectionsPage.Children.OfType<ScrollViewer>().Single();var heading=ConnectionsPage.Children.OfType<TextBlock>().Single();var y=heading.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y;
        body.ChangeView(null,1000,null,true);await Task.Delay(80);Root.UpdateLayout();Check(heading.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y==y,"integration-heading-pinned-on-scroll");body.ChangeView(null,0,null,true);
        Check(HelpPage.Children.OfType<TextBlock>().Any()&&SettingsPage.Children.OfType<TextBlock>().Any(),"other-scroll-page-headings-outside-scroll");
        await NavigateAsync("library");LibraryNav.ApplyTemplate();Root.UpdateLayout();var presenter=VisualTreeHelper.GetChild(LibraryNav,0) as ContentPresenter;var color=(presenter?.Background as SolidColorBrush)?.Color;
        VisualStateManager.GoToState(LibraryNav,"PointerOver",true);Root.UpdateLayout();Check(presenter is not null&&(presenter.Background as SolidColorBrush)?.Color==color,"selected-nav-hover-stays-identical");
        VisualStateManager.GoToState(LibraryNav,"Pressed",true);Root.UpdateLayout();Check((presenter!.Background as SolidColorBrush)?.Color==color,"selected-nav-press-stays-identical");VisualStateManager.GoToState(LibraryNav,"Normal",false);
        Check(AgentsNav.Style==(Style)Application.Current.Resources["NavButton"],"unselected-navigation-keeps-hover-style");
        var lightResources=Application.Current.Resources;
        Check((lightResources["ComboBoxDropDownBackground"] as SolidColorBrush)?.Color==(lightResources["MenuFlyoutPresenterBackground"] as SolidColorBrush)?.Color,"dropdowns-and-menus-share-paper");
        Check((CornerRadius)Application.Current.Resources["OverlayCornerRadius"]==new CornerRadius(5),"popup-corners-unified");
        KindFilter.IsDropDownOpen=true;await Task.Delay(160);Root.UpdateLayout();
        var popup=VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Single(x=>x.IsOpen);
        var popupParts=SmokeVisuals(popup.Child).OfType<Border>().ToArray();
        Check(popupParts.Any(x=>(x.Background as SolidColorBrush)?.Color==Windows.UI.Color.FromArgb(255,236,236,228)),"actual-dropdown-paper-background");
        Check(popupParts.Any(x=>x.CornerRadius.TopLeft==5&&x.CornerRadius.BottomRight==5),"actual-dropdown-small-corners");
        await RenderSnapshotAsync(Option("--smoke-report")+".dropdown.png",(FrameworkElement)popup.Child);KindFilter.IsDropDownOpen=false;
        var unchangedTime=File.GetLastWriteTimeUtc(source);await File.WriteAllTextAsync(source,before.Replace("needle","NEEDLE"));File.SetLastWriteTimeUtc(source,unchangedTime);await RefreshLibraryAsync();
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(!DetailMetadata.Text.Contains("变化"))await Task.Delay(20,timeout.Token);
        Check(!DetailPreview.Text.Contains("needle")&&DetailMetadata.Text.Contains("变化"),"detail-detects-equal-size-equal-time-replacement");
        await File.WriteAllTextAsync(source,"externally changed source");await RefreshLibraryAsync();var changed=(await store.GetAsync(parsed.Id))!;Check(changed.Preview==""&&changed.ExtractionStatus.Contains("变化"),"changed-source-invalidates-cached-preview");
        File.Delete(source);var missing=await store.GetAsync(parsed.Id);Check(missing!.Preview==""&&missing.ExtractionStatus.Contains("缺失"),"missing-source-no-stale-text");
        Check(smokeDialogAnswers.Count==0&&smokeMessages.Count==0,"all-confirmations-observed");
        await RunBrowserRegressionAsync(Check);
        return new{checks};
    }
    private async Task RunBrowserRegressionAsync(Action<bool,string> check)
    {
        var smokeWindow=WinRT.Interop.WindowNative.GetWindowHandle(this);
        if(!BrowserSmokeSetWindowPos(smokeWindow,new IntPtr(-1),0,0,0,0,0x43))throw new InvalidOperationException("Cannot show the isolated input test window");
        try
        {
        Activate();BrowserSmokeSetForegroundWindow(smokeWindow);
        void RequireForeground(){if(BrowserSmokeGetForegroundWindow()!=smokeWindow)throw new InvalidOperationException($"Browser input regression requires its own foreground window: expected={smokeWindow}, actual={BrowserSmokeGetForegroundWindow()}");}
        async Task FocusSmokeAsync()
        {
            if(BrowserSmokeGetForegroundWindow()!=smokeWindow)
            {
                var current=BrowserSmokeGetCurrentThreadId();
                var foreground=BrowserSmokeGetWindowThreadProcessId(BrowserSmokeGetForegroundWindow(),out _);
                var attached=foreground!=0&&foreground!=current&&BrowserSmokeAttachThreadInput(current,foreground,true);
                try{Activate();BrowserSmokeSetForegroundWindow(smokeWindow);}
                finally{if(attached)BrowserSmokeAttachThreadInput(current,foreground,false);}
                await Task.Delay(20);
            }
            RequireForeground();
        }
        void Send(params BrowserSmokeInput[] inputs)
        {
            // Check immediately before every batch, including the second double-click tap.
            // Down/up and modifier release travel in the same batch, without an await gap.
            RequireForeground();
            if(BrowserSmokeSendInput((uint)inputs.Length,inputs,Marshal.SizeOf<BrowserSmokeInput>())!=(uint)inputs.Length)
                throw new InvalidOperationException("Native browser input batch could not be injected");
        }
        static BrowserSmokeInput Keyboard(VirtualKey key,bool release=false)=>new(){Type=1,Data=new(){Keyboard=new(){Key=(ushort)key,Flags=release?2u:0u}}};
        async Task Wait(Func<bool> condition)
        {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));try{while(!condition()||operation is not null)await Task.Delay(25,timeout.Token);await Task.Delay(70,timeout.Token);Root.UpdateLayout();}catch(OperationCanceledException){throw new InvalidOperationException("Browser wait failed: "+browserSmokeKeyTrace);}}
        async Task Invoke(Button button,Func<bool> condition)
        {((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();await Wait(condition);}
        async Task Key(VirtualKey key,VirtualKey? modifier=null)
        {
            await FocusSmokeAsync();
            if(modifier is { } held)Send(Keyboard(held),Keyboard(key),Keyboard(key,true),Keyboard(held,true));
            else Send(Keyboard(key),Keyboard(key,true));
            await Task.Delay(120);await Wait(()=>true);
        }
        void FocusItems()
        {
            Root.UpdateLayout();
            var item=ArtifactList.SelectedItems.Cast<object>().LastOrDefault()??ArtifactList.Items.Cast<object>().FirstOrDefault();
            if(item is null)throw new InvalidOperationException("No grid item available for keyboard input");
            ArtifactList.ScrollIntoView(item);Root.UpdateLayout();
            if(ArtifactList.ContainerFromItem(item) is not Control container||!container.Focus(FocusState.Programmatic))
                throw new InvalidOperationException("The actual grid item could not receive keyboard focus");
        }
        FrameworkElement ItemElement(string name)
        {
            Root.UpdateLayout();var item=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title==name);
            ArtifactList.ScrollIntoView(item);Root.UpdateLayout();
            var container=(GridViewItem)ArtifactList.ContainerFromItem(item);
            return SmokeVisuals(container).OfType<TextBlock>().First(x=>x.Text==name);
        }
        async Task Mouse(FrameworkElement target,uint data,bool side,int taps=1)
        {
            await FocusSmokeAsync();
            RequireForeground();
            var point=target.TransformToVisual(Root).TransformPoint(new Point(target.ActualWidth/2,target.ActualHeight/2));
            var scale=Root.XamlRoot.RasterizationScale;var screen=new BrowserSmokePoint{X=(int)(point.X*scale),Y=(int)(point.Y*scale)};
            if(!BrowserSmokeClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this),ref screen))throw new InvalidOperationException("Native client coordinates unavailable");
            BrowserSmokeGetCursorPos(out var previous);
            try
            {
                RequireForeground();
                BrowserSmokeSetCursorPos(screen.X,screen.Y);
                var hit=BrowserSmokeWindowFromPoint(screen);
                if(BrowserSmokeGetAncestor(hit,2)!=smokeWindow)
                    throw new InvalidOperationException($"Input target is not the test window: point={screen.X},{screen.Y}; hit={hit}; root={BrowserSmokeGetAncestor(hit,2)}; expected={smokeWindow}; scale={scale}; window={AppWindow.Position}; rootSize={Root.ActualWidth},{Root.ActualHeight}");
                for(var i=0;i<taps;i++)
                {
                    Send(new(){Data=new(){Mouse=new(){Flags=side?0x80u:0x2u,Data=data}}},new(){Data=new(){Mouse=new(){Flags=side?0x100u:0x4u,Data=data}}});
                    await Task.Delay(55);
                }
                await Task.Delay(100);await Wait(()=>true);
            }
            finally{if(BrowserSmokeGetForegroundWindow()==smokeWindow)BrowserSmokeSetCursorPos(previous.X,previous.Y);}
        }
        check(browserInputAttached,"browser-root-routed-handlers-installed");
        var project=SourceStore.CodexProjects.Single();var reports=Path.Combine(project.RootPath,"reports");
        var branch=Path.Combine(project.RootPath,"branch");Directory.CreateDirectory(branch);
        await File.WriteAllTextAsync(Path.Combine(project.RootPath,"a.txt"),"a");await File.WriteAllTextAsync(Path.Combine(project.RootPath,"b.txt"),"bb");
        await NavigateAsync("library");await ShowBrowserAsync(null);await ShowBrowserAsync(project);await ShowBrowserAsync(project,reports);
        check(BrowserBackButton.IsEnabled&&!BrowserForwardButton.IsEnabled,"history-buttons-track-actual-location");
        // Send real Windows input over a child that marks PointerPressed handled. Root must
        // still see XButton1/2 through its handledEventsToo registration. This is synthetic
        // OS input, not evidence of a physical mouse/driver test.
        var target=ItemElement("nested.md");var childHandled=false;
        var pointerTrace="none";
        PointerEventHandler trace=(_,e)=>
        {
            var names=new List<string>();
            for(var node=e.OriginalSource as DependencyObject;node is not null;node=VisualTreeHelper.GetParent(node))names.Add(node.GetType().Name+":"+(node as FrameworkElement)?.Name);
            pointerTrace=e.GetCurrentPoint(Root).Properties.PointerUpdateKind+" "+string.Join("/",names);
        };
        Root.AddHandler(UIElement.PointerPressedEvent,trace,true);
        await RenderSnapshotAsync(Option("--smoke-report")+".browser-input.png");
        // The item container owns hit testing; decorative TextBlocks need not be
        // ancestors of the actual PointerPressed source in a GridView template.
        var handledChild=(GridViewItem)ArtifactList.ContainerFromItem(ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title=="nested.md"));
        PointerEventHandler swallow=(_,e)=>{childHandled=true;e.Handled=true;};handledChild.AddHandler(UIElement.PointerPressedEvent,swallow,true);
        try{await Mouse(target,1,true);await Wait(()=>browserDirectory==project.RootPath);}
        finally{handledChild.RemoveHandler(UIElement.PointerPressedEvent,swallow);Root.RemoveHandler(UIElement.PointerPressedEvent,trace);}
        if(!childHandled||browserDirectory!=project.RootPath||!BrowserForwardButton.IsEnabled)
            throw new InvalidOperationException($"xbutton1 diagnostic: child={childHandled}; root={browserDirectory==project.RootPath}; forward={BrowserForwardButton.IsEnabled}; {pointerTrace}");
        check(childHandled&&browserDirectory==project.RootPath&&BrowserForwardButton.IsEnabled,"xbutton1-handled-child-routes-back");
        await Mouse(ItemElement("reports"),2,true);await Wait(()=>browserDirectory==reports);
        check(browserDirectory==reports,"xbutton2-routes-forward");
        await Invoke(BrowserBackButton,()=>browserDirectory==project.RootPath);await ShowBrowserAsync(project,branch);
        check(!BrowserForwardButton.IsEnabled,"branch-navigation-discards-future");
        await Invoke(BrowserUpButton,()=>browserDirectory==project.RootPath);
        check(browserDirectory==project.RootPath,"up-toolbar-visits-parent");
        await Mouse(ItemElement("reports"),0,false);
        check(browserDirectory==project.RootPath&&ArtifactList.SelectedItems.OfType<ArtifactItem>().Any(x=>x.Title=="reports"),"single-click-selects-without-opening-directory");
        await Task.Delay((int)BrowserSmokeGetDoubleClickTime()+50);
        await Mouse(ItemElement("reports"),0,false,2);await Wait(()=>browserDirectory==reports);
        check(browserDirectory==reports,"actual-double-tap-opens-directory");
        FocusItems();await Key(VirtualKey.Left,VirtualKey.Menu);await Wait(()=>browserDirectory==project.RootPath);
        check(browserDirectory==project.RootPath,"alt-left-routed-from-grid");
        await Key(VirtualKey.Right,VirtualKey.Menu);await Wait(()=>browserDirectory==reports);
        check(browserDirectory==reports,"alt-right-routed-from-grid");
        await Key(VirtualKey.Up,VirtualKey.Menu);await Wait(()=>browserDirectory==project.RootPath);
        check(browserDirectory==project.RootPath,"alt-up-routed-from-grid");
        await ShowBrowserAsync(project,branch);await ShowBrowserAsync(project,reports);FocusItems();await Key(VirtualKey.Back);await Wait(()=>browserDirectory==branch);
        check(browserDirectory==branch,"backspace-returns-to-sibling-history-not-parent");
        await ShowBrowserAsync(project);ArtifactList.SelectedItem=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title=="reports");FocusItems();await Key(VirtualKey.Enter);await Wait(()=>browserDirectory==reports);
        check(browserDirectory==reports,"enter-opens-selected-valid-directory");
        var nested=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title=="nested.md");ArtifactList.SelectedItem=nested;FocusItems();var indexedBeforeEnter=(await store.ListAsync()).Count;
        await Key(VirtualKey.Enter);
        check(browserDirectory==reports&&selectedArtifactId==nested.Record.Id&&DetailTitle.Text=="nested.md"&&(await store.ListAsync()).Count==indexedBeforeEnter,"enter-file-keeps-selection-detail-without-parsing-or-launch");
        await ShowBrowserAsync(project);
        SearchBox.Focus(FocusState.Programmatic);SearchBox.Text="ab";SearchBox.Select(1,0);await Key(VirtualKey.Back);
        check(SearchBox.Text=="b"&&browserDirectory==project.RootPath,"textbox-backspace-edits-without-navigation");
        await Key(VirtualKey.A,VirtualKey.Control);await Key(VirtualKey.C,VirtualKey.Control);
        check(SearchBox.SelectedText=="b"&&await Clipboard.GetContent().GetTextAsync()=="b","textbox-select-all-copy-not-hijacked");
        await Key(VirtualKey.Left,VirtualKey.Menu);
        check(browserDirectory==project.RootPath,"textbox-alt-left-does-not-route-history");
        await Key(VirtualKey.Enter);
        check(browserDirectory==project.RootPath&&SearchBox.Text=="b","textbox-enter-does-not-open-browser-selection");
        var priorUpdating=updating;updating=true;SearchBox.Text="";updating=priorUpdating;await RefreshLibraryAsync();
        FocusItems();ArtifactList.SelectedItems.Clear();
        var files=ArtifactList.Items.OfType<ArtifactItem>().Where(x=>!x.IsDirectory).ToArray();foreach(var file in files)ArtifactList.SelectedItems.Add(file);
        var ids=ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Record.Id).ToHashSet();
        await Key(VirtualKey.F5);
        check(ids.SetEquals(ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Record.Id)),"f5-preserves-multiple-selected-identities");
        await Invoke(SortDirectionButton,()=>true);
        check(ids.SetEquals(ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Record.Id)),"sort-preserves-multiple-selected-identities");
        check(ArtifactList.Items.OfType<ArtifactItem>().TakeWhile(x=>x.IsDirectory).Count()==2,"descending-sort-still-folders-first");
        FocusItems();await Key(VirtualKey.C,VirtualKey.Control);
        var copiedText=await Clipboard.GetContent().GetTextAsync();
        var copied=copiedText.Replace("\r\n","\n").Split('\n',StringSplitOptions.RemoveEmptyEntries);
        // The earlier integration fixture also creates AGENTS.md and its lock. They
        // remain visible metadata, but copying their paths must respect exclusions.
        var validPaths=new[]{Path.Combine(project.RootPath,"a.txt"),Path.Combine(project.RootPath,"b.txt")};
        if(!copied.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(validPaths))
            throw new InvalidOperationException("Clipboard differs: "+JsonSerializer.Serialize(new{copied,expected=validPaths,selected=ArtifactList.SelectedItems.Count,keys=browserSmokeKeyTrace}));
        check(copied.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(validPaths),"ctrl-c-copies-only-selected-valid-paths");
        await Key(VirtualKey.A,VirtualKey.Control);
        check(ArtifactList.SelectedItems.Count==ArtifactList.Items.Count&&LibraryCount.Text.Contains("已选"),"ctrl-a-status-count-matches-selection");
        await ShowBrowserAsync(project,branch);Directory.Delete(branch);await RefreshLibraryAsync();
        check(browserDirectory==project.RootPath,"missing-current-directory-refreshes-to-parent");
        Directory.CreateDirectory(Path.Combine(project.RootPath,".git"));await ShowBrowserAsync(project,Path.Combine(project.RootPath,".git"));
        check(browserDirectory==project.RootPath,"excluded-directory-cannot-enter-browser");
        ArtifactList.SelectedItem=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title==".git");FocusItems();await Key(VirtualKey.Enter);
        check(browserDirectory==project.RootPath,"enter-revalidates-and-rejects-excluded-directory");
        var staleFolder=Path.Combine(project.RootPath,"stale-folder");Directory.CreateDirectory(staleFolder);await RefreshLibraryAsync();
        ArtifactList.SelectedItem=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title=="stale-folder");Directory.Delete(staleFolder);FocusItems();await Key(VirtualKey.Enter);
        check(browserDirectory==project.RootPath,"enter-revalidates-and-rejects-missing-selected-directory");
        await ShowBrowserAsync(project,Path.GetDirectoryName(project.RootPath));
        check(browserDirectory==project.RootPath,"outside-project-navigation-is-rejected");

        // Both cards come from a real isolated Codex registry, with existing directories.
        var longRoot=Path.Combine(project.RootPath,"长路径_"+new string('长',70));Directory.CreateDirectory(longRoot);
        var registry=Path.Combine(integration.CodexHome,".codex-global-state.json");
        await File.WriteAllTextAsync(registry,JsonSerializer.Serialize(new Dictionary<string,object>{{"local-projects",new Dictionary<string,object>{{"short",new{name="短",rootPaths=new[]{project.RootPath}}},{"long",new{name=new string('长',80),rootPaths=new[]{longRoot}}}}}}));
        await SourceStore.RefreshCodexProjectsAsync();await RefreshProjectsAsync();await NavigateAsync("projects");Root.UpdateLayout();await Task.Delay(100);Root.UpdateLayout();
        var containers=projects.Select(p=>(GridViewItem)ProjectsList.ContainerFromItem(p)).ToArray();
        var cards=containers.Select(c=>SmokeVisuals(c).OfType<Border>().Single(x=>x.Width==242&&x.Height==144)).ToArray();
        check(containers.Length==2&&containers.All(x=>Math.Abs(x.ActualWidth-248)<.5&&Math.Abs(x.ActualHeight-150)<.5),"short-long-project-actual-container-size-equal");
        check(cards.All(x=>Math.Abs(x.ActualWidth-242)<.5&&Math.Abs(x.ActualHeight-144)<.5&&x.CornerRadius.TopLeft==5&&x.Clip is RectangleGeometry),"short-long-project-actual-card-size-and-clipping-equal");
        var title=ProjectsPage.Children.OfType<Grid>().Single().Children.OfType<TextBlock>().Single();var titleY=title.TransformToVisual(Root).TransformPoint(new()).Y;
        var width=ProjectsList.Width;var alignment=ProjectsList.HorizontalAlignment;
        try
        {
            ProjectsList.Width=260;ProjectsList.HorizontalAlignment=HorizontalAlignment.Left;Root.UpdateLayout();
            var first=containers[0].TransformToVisual(ProjectsList).TransformPoint(new());var second=containers[1].TransformToVisual(ProjectsList).TransformPoint(new());
            check(second.Y>first.Y&&Math.Abs(second.X-first.X)<.5&&cards.All(x=>Math.Abs(x.ActualWidth-242)<.5),"project-cards-wrap-at-narrow-viewport-with-fixed-size");
            check(title.TransformToVisual(Root).TransformPoint(new()).Y==titleY,"project-title-pinned-while-grid-reflows");
        }
        finally{ProjectsList.Width=width;ProjectsList.HorizontalAlignment=alignment;Root.UpdateLayout();}
        await NavigateAsync("library");await ShowBrowserAsync(project);
        await File.WriteAllTextAsync(registry,"{\"local-projects\":{}}");await SourceStore.RefreshCodexProjectsAsync();await RefreshProjectsAsync();await RefreshLibraryAsync();
        check(browserProject is null&&browserDirectory is null&&ArtifactList.Items.Count==0,"removed-project-refresh-falls-back-to-all-projects");
        await ExecuteBrowserCommandAsync(BrowserCommand.Back);
        check(browserProject is null,"history-cannot-reenter-removed-project");
        }
        finally{BrowserSmokeSetWindowPos(smokeWindow,new IntPtr(-2),0,0,0,0,0x13);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct BrowserSmokePoint { public int X;public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BrowserSmokeInput { public uint Type;public BrowserSmokeInputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct BrowserSmokeInputData
    {
        [FieldOffset(0)] public BrowserSmokeMouseInput Mouse;
        [FieldOffset(0)] public BrowserSmokeKeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BrowserSmokeMouseInput { public int X;public int Y;public uint Data;public uint Flags;public uint Time;public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct BrowserSmokeKeyboardInput { public ushort Key;public ushort Scan;public uint Flags;public uint Time;public UIntPtr Extra; }
    [DllImport("user32.dll",EntryPoint="SendInput",SetLastError=true)] private static extern uint BrowserSmokeSendInput(uint count,[In] BrowserSmokeInput[] inputs,int size);
    [DllImport("user32.dll",EntryPoint="SetCursorPos")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeSetCursorPos(int x,int y);
    [DllImport("user32.dll",EntryPoint="GetCursorPos")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeGetCursorPos(out BrowserSmokePoint point);
    [DllImport("user32.dll",EntryPoint="ClientToScreen")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeClientToScreen(IntPtr window,ref BrowserSmokePoint point);
    [DllImport("user32.dll",EntryPoint="GetForegroundWindow")] private static extern IntPtr BrowserSmokeGetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="SetForegroundWindow")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeSetForegroundWindow(IntPtr window);
    [DllImport("kernel32.dll",EntryPoint="GetCurrentThreadId")] private static extern uint BrowserSmokeGetCurrentThreadId();
    [DllImport("user32.dll",EntryPoint="GetWindowThreadProcessId")] private static extern uint BrowserSmokeGetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",EntryPoint="AttachThreadInput")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeAttachThreadInput(uint from,uint to,[MarshalAs(UnmanagedType.Bool)]bool attach);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool BrowserSmokeSetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="WindowFromPoint")] private static extern IntPtr BrowserSmokeWindowFromPoint(BrowserSmokePoint point);
    [DllImport("user32.dll",EntryPoint="GetAncestor")] private static extern IntPtr BrowserSmokeGetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetDoubleClickTime")] private static extern uint BrowserSmokeGetDoubleClickTime();
    private static IEnumerable<DependencyObject> SmokeVisuals(DependencyObject root)
    {yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in SmokeVisuals(VisualTreeHelper.GetChild(root,i)))yield return child;}
}
