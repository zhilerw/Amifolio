using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    // Opt-in developer checks use real compositor animations, not a simulated clock.
    private async Task<object> RunMotionRegressionAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native motion regression: " + name);
            checks.Add(name);
        }
        var probe = new Border
        {
            Width = 240, Height = 86, CornerRadius = new CornerRadius(5),
            Background = (Brush)Application.Current.Resources["AccentTint"],
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "动效验证", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Grid.SetRow(probe, 1);
        Root.Children.Add(probe);
        Root.UpdateLayout();
        using var effects = new MotionController(Root);
        effects.SetTestPreference(true);
        async Task SettledAsync()
        {
            var deadline = Stopwatch.StartNew();
            while (effects.ActiveCount != 0 && deadline.ElapsedMilliseconds < 3000) await Task.Delay(15);
            Check(effects.ActiveCount == 0, "finite animation retires without a frame loop");
        }
        try
        {
            await RunAsync("短任务验证", _ => { StatusText.Text = "就绪"; return Task.CompletedTask; });
            await Task.Delay(220);
            Check(OperationNotice.Visibility == Visibility.Collapsed && !noticeTimer.IsEnabled, "short tasks do not flash a progress notification or retain its timer");
            await RunAsync("长任务验证", async _ =>
            {
                await Task.Delay(300);
                Check(OperationNotice.Visibility == Visibility.Visible, "long tasks reveal progress after the debounce interval");
                StatusText.Text = "就绪";
            });
            var noticeExit = Stopwatch.StartNew();
            while (OperationNotice.Visibility != Visibility.Collapsed && noticeExit.ElapsedMilliseconds < 2500) await Task.Delay(15);
            Check(OperationNotice.Visibility == Visibility.Collapsed && !noticeTimer.IsEnabled,
                $"completed task clears its progress surface and timer (visible={OperationNotice.Visibility}, timer={noticeTimer.IsEnabled}, active={motion?.ActiveCount}, enabled={motion?.Enabled})");
            noticeExit.Stop();
            await RunAsync("重复任务验证", _ => Task.CompletedTask);
            motion?.SetVisible(OperationNotice, false, MotionKind.Notice);
            await Task.Delay(300);
            await RunAsync("重复任务验证", async _ =>
            {
                await Task.Delay(300);
                Check(OperationNotice.Visibility == Visibility.Visible, "reusing an unchanged task label still reveals delayed progress");
                StatusText.Text = "就绪";
            });
            await Task.Delay(300);
            var originalSize = (probe.ActualWidth, probe.ActualHeight);
            var elapsed = Stopwatch.StartNew();
            effects.Enter(probe, MotionKind.Page);
            elapsed.Stop();
            Check(effects.ActiveCount == 1, "entry starts a real compositor batch immediately");
            var visual = ElementCompositionPreview.GetElementVisual(probe);
            Check(visual.TryGetAnimationController("Opacity") is not null, "opacity animation is attached to the compositor");
            Check(visual.Properties.TryGetAnimationController("Translation") is not null, "translation animation is attached without changing layout");
            await SettledAsync();
            Check(visual.Opacity == 1 && visual.Properties.TryGetVector3("Translation", out var translation) == Microsoft.UI.Composition.CompositionGetValueStatus.Succeeded && translation == Vector3.Zero, "entry restores exact final opacity and translation");
            Check((probe.ActualWidth, probe.ActualHeight) == originalSize, "entry leaves layout dimensions unchanged");

            for (var i = 0; i < 80; i++) effects.Enter(probe, MotionKind.Detail);
            Check(effects.ActiveCount == 1 && effects.PeakCount == 1, "rapid repeated requests replace rather than queue animations");
            await SettledAsync();
            effects.SetVisible(probe, false, MotionKind.Notice);
            Check(probe.Visibility == Visibility.Visible && effects.ActiveCount == 1, "notification exit retains its surface until completion");
            await Task.Delay(20);
            effects.SetVisible(probe, true, MotionKind.Notice);
            await SettledAsync();
            await Task.Delay(150);
            Check(probe.Visibility == Visibility.Visible, "stale exit callback cannot hide a newer notification");
            effects.SetVisible(probe, false, MotionKind.Notice);
            await SettledAsync();
            Check(probe.Visibility == Visibility.Collapsed && visual.Opacity == 1, "completed exit collapses and resets the notification");

            effects.SetVisible(probe, true, MotionKind.Edit);
            effects.SetVisible(probe, false, MotionKind.Edit);
            Check(probe.Visibility == Visibility.Collapsed && effects.ActiveCount == 0, "obsolete edit controls collapse immediately");
            effects.SetVisible(probe, true, MotionKind.Notice);
            effects.SetTestPreference(false);
            Check(effects.ActiveCount == 0 && visual.Opacity == 1 && probe.Visibility == Visibility.Visible, "reduced motion settles an in-flight entrance immediately");
            effects.SetVisible(probe, false, MotionKind.Notice);
            Check(probe.Visibility == Visibility.Collapsed && effects.ActiveCount == 0, "reduced motion hides without animation");
            effects.SetVisible(probe, true, MotionKind.Page);
            var fresh = new Border();
            effects.Enter(fresh, MotionKind.Page);
            Check(probe.Visibility == Visibility.Visible && fresh.Visibility == Visibility.Visible && effects.ActiveCount == 0,
                "reduced motion keeps existing and previously unanimated content immediately available");
            effects.SetTestPreference(true);
            effects.SetVisible(probe, false, MotionKind.Notice);
            effects.SetWindowActive(false);
            Check(effects.ActiveCount == 0 && probe.Visibility == Visibility.Collapsed, "window deactivation settles pending exits");
            effects.SetVisible(probe, true, MotionKind.Page);
            Check(effects.ActiveCount == 0 && probe.Visibility == Visibility.Visible, "inactive window does not start new animations");
            effects.SetWindowActive(true);

            // Only this developer mode samples the process; production adds no sampling timer.
            async Task<object> SampleIdleAsync()
            {
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                await Task.Delay(2500);
                process.Refresh();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                return new { wallMs = clock.Elapsed.TotalMilliseconds, cpuMs, cpuPercentOfOneCore = cpuMs / clock.Elapsed.TotalMilliseconds * 100,
                    privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64, activeTransitions = effects.ActiveCount };
            }
            await Task.Delay(400);
            var idleBefore = await SampleIdleAsync();
            var stress = Stopwatch.StartNew();
            for (var i = 0; i < 240; i++)
            {
                effects.SetVisible(probe, false, MotionKind.Notice);
                effects.SetVisible(probe, true, MotionKind.Notice);
                if (i % 12 == 0) await Task.Delay(8);
            }
            stress.Stop();
            await SettledAsync();
            var idleAfter = await SampleIdleAsync();
            var stableIdle = await SampleIdleAsync();
            Check(probe.Visibility == Visibility.Visible && effects.ActiveCount == 0 && effects.PeakCount == 1, "notification stress leaves one visible surface and no pending animations");
            Check(effects.CachedProfileCount <= 6, "animation templates stay bounded after hundreds of replacements");

            effects.SetVisible(probe, false, MotionKind.Notice);
            effects.Dispose();
            Check(effects.ActiveCount == 0 && probe.Visibility == Visibility.Collapsed, "dispose completes pending exit and releases batches");
            effects.Enter(probe, MotionKind.Page);
            await Task.Delay(200);
            Check(effects.ActiveCount == 0 && probe.Visibility == Visibility.Collapsed, "disposed controller rejects later requests and stale callbacks");
            return new { checks, animationsStarted = effects.StartedCount, peakActive = effects.PeakCount, firstRequestMs = elapsed.Elapsed.TotalMilliseconds,
                noticeExitMs = noticeExit.Elapsed.TotalMilliseconds,
                notificationStress = new { requests = 480, elapsedMs = stress.Elapsed.TotalMilliseconds }, idleBefore, idleAfter, stableIdle,
                systemAnimationSettingChanged = false, note = "Process samples on this machine, not frame-rate or cross-machine performance acceptance." };
        }
        finally { Root.Children.Remove(probe); }
    }
}
