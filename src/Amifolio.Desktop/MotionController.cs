using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Amifolio.Desktop;

internal enum MotionKind { Page, Detail, Edit, Notice, Selection }

// Finite, compositor-only transitions. No frame callbacks, layout animation or idle timer.
internal sealed class MotionController : IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private readonly UISettings settings = new();
    private readonly Dictionary<FrameworkElement, Pending> running = new();
    private readonly Compositor compositor;
    private readonly CubicBezierEasingFunction easing;
    private readonly Dictionary<(MotionKind Kind, bool Hide), (ScalarKeyFrameAnimation Fade, Vector3KeyFrameAnimation Move)> animations = new();
    private bool systemEnabled;
    private bool windowActive = true;
    private bool disposed;
    private bool? testPreference;

    public MotionController(FrameworkElement root)
    {
        dispatcher = root.DispatcherQueue;
        compositor = ElementCompositionPreview.GetElementVisual(root).Compositor;
        easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(0, 1));
        systemEnabled = settings.AnimationsEnabled;
        settings.AnimationsEnabledChanged += SettingsChanged;
    }

    public bool Enabled => !disposed && windowActive && (testPreference ?? systemEnabled);
    public int ActiveCount => running.Count;
    public int StartedCount { get; private set; }
    public int PeakCount { get; private set; }
    internal int CachedProfileCount => animations.Count;

    public void Enter(FrameworkElement element, MotionKind kind)
    {
        if (disposed || element.Visibility != Visibility.Visible) return;
        Animate(element, kind, hide: false);
    }

    public void SetVisible(FrameworkElement element, bool visible, MotionKind kind)
    {
        if (disposed) return;
        var exiting = running.TryGetValue(element, out var pending) && pending.Hide;
        if (visible)
        {
            if (element.Visibility == Visibility.Visible && !exiting) return;
            element.Visibility = Visibility.Visible;
            Animate(element, kind, hide: false);
        }
        else
        {
            if (element.Visibility == Visibility.Collapsed || exiting) return;
            // Editing controls disappear immediately so an obsolete Cancel cannot receive focus.
            if (kind == MotionKind.Edit) { Stop(element); element.Visibility = Visibility.Collapsed; }
            else Animate(element, kind, hide: true);
        }
    }

    public void Stop(FrameworkElement element)
    {
        if (!running.Remove(element, out var pending)) return;
        Release(pending);
        Reset(element);
    }

    public void SetWindowActive(bool active)
    {
        windowActive = active;
        if (!Enabled) SettleAll();
    }

    // Only invoked by the explicit --smoke-mode motion developer regression.
    internal void SetTestPreference(bool? enabled)
    {
        testPreference = enabled;
        if (!Enabled) SettleAll();
    }

    private void SettingsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => dispatcher.TryEnqueue(() =>
        {
            if (disposed) return;
            systemEnabled = settings.AnimationsEnabled;
            if (!Enabled) SettleAll();
        });

    private void Animate(FrameworkElement element, MotionKind kind, bool hide)
    {
        var replacing = running.Remove(element, out var old);
        if (replacing) Release(old!);
        if (!Enabled)
        {
            // A fresh element has no compositor Translation property until its first animation.
            if (replacing) Reset(element);
            if (hide) element.Visibility = Visibility.Collapsed;
            return;
        }

        var (_, opacity, distance) = Profile(kind);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!replacing)
        {
            visual.Opacity = hide ? 1 : opacity;
            visual.Properties.InsertVector3("Translation", hide ? Vector3.Zero : new Vector3(0, distance, 0));
        }
        // StartAnimation copies the template; one cached pair can serve multiple targets safely.
        var (fade, translation) = AnimationsFor(kind, hide);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var next = new Pending(batch, hide);
        void Complete()
        {
            if (!running.TryGetValue(element, out var current) || !ReferenceEquals(current, next)) return;
            running.Remove(element);
            Release(next);
            Reset(element);
            if (hide) element.Visibility = Visibility.Collapsed;
        }
        next.Handler = (_, _) =>
        {
            if (dispatcher.HasThreadAccess) Complete();
            else dispatcher.TryEnqueue(Complete);
        };
        batch.Completed += next.Handler;
        running[element] = next;
        visual.StartAnimation("Opacity", fade);
        visual.Properties.StartAnimation("Translation", translation);
        batch.End();
        StartedCount++;
        PeakCount = Math.Max(PeakCount, running.Count);
    }

    private static (int Milliseconds, float Opacity, float Distance) Profile(MotionKind kind) => kind switch
        {
            MotionKind.Page => (160, .94f, 4f),
            MotionKind.Detail => (120, .92f, 3f),
            MotionKind.Edit => (100, .90f, 2f),
            MotionKind.Selection => (90, .97f, 0f),
            _ => (140, .65f, -4f)
        };

    private (ScalarKeyFrameAnimation Fade, Vector3KeyFrameAnimation Move) AnimationsFor(MotionKind kind, bool hide)
    {
        if (animations.TryGetValue((kind, hide), out var cached)) return cached;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(hide ? 100 : Profile(kind).Milliseconds);
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, hide ? 0 : 1, easing);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.Duration = fade.Duration;
        translation.InsertExpressionKeyFrame(0, "this.StartingValue");
        translation.InsertKeyFrame(1, hide ? new Vector3(0, -2, 0) : Vector3.Zero, easing);
        animations[(kind, hide)] = (fade, translation);
        return (fade, translation);
    }

    private static void Reset(FrameworkElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = 1;
        visual.Properties.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private static void Release(Pending pending)
    {
        pending.Batch.Completed -= pending.Handler;
        pending.Batch.Dispose();
    }

    private void SettleAll()
    {
        foreach (var (element, pending) in running.ToArray())
        {
            running.Remove(element);
            Release(pending);
            Reset(element);
            if (pending.Hide) element.Visibility = Visibility.Collapsed;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        settings.AnimationsEnabledChanged -= SettingsChanged;
        SettleAll();
        foreach (var (fade, move) in animations.Values) { fade.Dispose(); move.Dispose(); }
        animations.Clear();
        easing.Dispose();
    }

    private sealed class Pending(CompositionScopedBatch batch, bool hide)
    {
        public CompositionScopedBatch Batch { get; } = batch;
        public bool Hide { get; } = hide;
        public TypedEventHandler<object, CompositionBatchCompletedEventArgs> Handler { get; set; } = null!;
    }
}
