using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 页面和货架的短促进场：淡入配合轻上浮，只动合成层，不改变布局、RenderTransform 或可见性。
/// 系统关闭动画时直接落定；重新播放、离树和播放结束都清掉旧动画，虚拟化容器不会带回半透明状态。
/// </summary>
internal static class HomeMotion
{
    private const int Duration = 440;
    private const int Stagger = 65;
    private static readonly UISettings Settings = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Active = new();
    private static readonly ConditionalWeakTable<FrameworkElement, RoutedEventHandler> Waiting = new();
    private static readonly ConditionalWeakTable<ItemsRepeater, ShelfEntrance> Shelves = new();

    internal static bool AnimationsEnabled => Settings.AnimationsEnabled;

    /// <summary>只让已经实现的货架依次进场，绝不为了动画强制创建视口外的卡片。</summary>
    internal static void Enter(FrameworkElement? root, ItemsRepeater? repeater, int count)
    {
        if (repeater is null) return;

        // Loaded 往往早于服务端数据；即使此刻 count 为零，也监听随后实现的货架。
        if (root?.XamlRoot is not null && !Shelves.TryGetValue(repeater, out _))
            Shelves.Add(repeater, new ShelfEntrance(root, repeater));

        for (var row = 0; row < count; row++)
        {
            if (repeater.TryGetElement(row) is not FrameworkElement shelf) continue;

            if (root?.XamlRoot is null) Stop(shelf);
            else RevealWhenLoaded(shelf, Math.Min(row, 5) * Stagger);
        }
    }

    internal static void Reveal(FrameworkElement element) => Reveal(element, 0);

    private static void RevealWhenLoaded(FrameworkElement element, int delay)
    {
        if (element.IsLoaded)
        {
            Reveal(element, delay);
            return;
        }

        Stop(element);
        RoutedEventHandler handler = (_, _) => Reveal(element, delay);
        Waiting.Add(element, handler);
        element.Loaded += handler;
    }

    private static void Reveal(FrameworkElement element, int delay)
    {
        Stop(element);
        if (element.XamlRoot is null || !AnimationsEnabled) return;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var compositor = visual.Compositor;
        using var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(Duration);
        fade.DelayTime = TimeSpan.FromMilliseconds(delay);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        using var rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 20, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, ease);
        rise.Duration = fade.Duration;
        rise.DelayTime = fade.DelayTime;
        rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var motion = new Motion(visual, batch);
        Active.Add(element, motion);
        element.Unloaded += OnUnloaded;
        batch.Completed += (_, _) =>
        {
            if (Active.TryGetValue(element, out var current) && ReferenceEquals(current, motion)) Stop(element);
        };

        visual.StartAnimation("Opacity", fade);
        // Offset 由 XAML 的布局拥有；Translation 是专门给视觉位移的独立合成属性。
        visual.StartAnimation("Translation", rise);
        batch.End();
    }

    /// <summary>恢复最终画面；没有正在播放的动画时也必须保持不透明，尤其是离树的自检实例。</summary>
    internal static void Stop(FrameworkElement element)
    {
        element.Unloaded -= OnUnloaded;
        if (Waiting.TryGetValue(element, out var handler))
        {
            element.Loaded -= handler;
            Waiting.Remove(element);
        }
        element.Opacity = 1;

        // 首次 Reveal 也会先走 Stop。Translation 只有启用后才是合法的合成属性，
        // 因此没有活动记录时不访问 Visual，更不能对尚未启用的属性调用 StopAnimation。
        if (!Active.TryGetValue(element, out var motion)) return;

        // 先摘下记录，停止或释放批次引发的完成回调便不会再次清理同一份动画。
        Active.Remove(element);
        var visual = motion.Visual;
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        motion.Batch.Dispose();
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Stop(element);
    }

    private sealed record Motion(Visual Visual, CompositionScopedBatch Batch);

    /// <summary>监听只活到页面离树；元素回收时取消待播放和正在播放的动画。</summary>
    private sealed class ShelfEntrance
    {
        private readonly FrameworkElement _root;
        private readonly ItemsRepeater _repeater;

        internal ShelfEntrance(FrameworkElement root, ItemsRepeater repeater)
        {
            _root = root;
            _repeater = repeater;
            root.Unloaded += OnRootUnloaded;
            repeater.ElementPrepared += OnPrepared;
            repeater.ElementClearing += OnClearing;
        }

        private void OnPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            if (_root.XamlRoot is not null && args.Element is FrameworkElement element)
                RevealWhenLoaded(element, Math.Min(args.Index, 5) * Stagger);
        }

        private static void OnClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is FrameworkElement element) Stop(element);
        }

        private void OnRootUnloaded(object sender, RoutedEventArgs e)
        {
            _root.Unloaded -= OnRootUnloaded;
            _repeater.ElementPrepared -= OnPrepared;
            _repeater.ElementClearing -= OnClearing;
            Shelves.Remove(_repeater);

            var count = _repeater.ItemsSourceView?.Count ?? 0;
            for (var index = 0; index < count; index++)
                if (_repeater.TryGetElement(index) is FrameworkElement element) Stop(element);
        }
    }
}
