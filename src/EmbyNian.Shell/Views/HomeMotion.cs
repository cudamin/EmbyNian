using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 页面和货架的短促进场：淡入配合轻上浮，只动 Opacity 与 RenderTransform，不改变布局或可见性。
/// 系统关闭动画时直接落定；重新播放、离树和播放结束都清掉旧动画，虚拟化容器不会带回半透明状态。
/// </summary>
internal static class HomeMotion
{
    private const int DurationMs = 440;
    private const int StaggerMs = 65;
    private static readonly UISettings Settings = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Active = new();
    private static readonly ConditionalWeakTable<FrameworkElement, RoutedEventHandler> Waiting = new();
    private static readonly ConditionalWeakTable<ItemsRepeater, ShelfEntrance> Shelves = new();

    internal static bool AnimationsEnabled => Settings.AnimationsEnabled;

    /// <summary>
    /// 进场动画真正落在的元素：ItemsRepeater 直接子的第一个视觉孩子（模板根）。直接子本身 —— 不管是
    /// 框架包的 ContentPresenter 还是模板根被直接当直接子用 —— 是框架 arrange/viewport 要操纵的对象，
    /// 碰不得（见 <see cref="Reveal"/> 里的占用冲突）。模板还没应用（直接子没有视觉孩子）时返回
    /// null，调用方放弃这一次动画：宁可少一段入场，不可碰直接子。Reveal/Stop 一律经由这里拿目标，
    /// 别处不许再摸 Repeater 的直接子。
    /// </summary>
    private static FrameworkElement? TargetOf(FrameworkElement repeaterChild)
    {
        return VisualTreeHelper.GetChildrenCount(repeaterChild) > 0
            ? VisualTreeHelper.GetChild(repeaterChild, 0) as FrameworkElement
            : null;
    }

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

            var target = TargetOf(shelf);
            if (target is null) continue;

            if (root?.XamlRoot is null) Stop(target);
            else RevealWhenLoaded(target, Math.Min(row, 5) * StaggerMs);
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

        // 2026-09-16 十六报：这一段原来是 composition 的 SetIsTranslationEnabled + GetElementVisual +
        // StartAnimation。系统窗口动画被打开的这一天现形 —— 动画关着的时候这里全部短路，雷埋着不响；
        // 一旦真跑，元素被 GetElementVisual 占用之后，WinUI 3 的渲染遍还要对同一份 visual 调 Scale，
        // 便以「Calling Scale API is not allowed ... GetElementVisual property in use」告终，界面线程
        // 未处理异常当场带走进程（走到「主页」时崩，八个闸门日全绿、一个系统开关全崩）。改走 XAML
        // Storyboard：Opacity 与 RenderTransform 都不参与布局、不占用 composition，与 HomeBanner 的
        // Rise/Push 同一条路。
        var drift = DriftFor(element);
        if (drift is not null) drift.Y = 20;
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delay);
        var duration = new Duration(TimeSpan.FromMilliseconds(DurationMs));
        var board = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            BeginTime = begin,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);

        if (drift is not null)
        {
            var rise = new DoubleAnimation
            {
                From = 20,
                To = 0,
                Duration = duration,
                BeginTime = begin,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(rise, drift);
            Storyboard.SetTargetProperty(rise, "Y");
            board.Children.Add(rise);
        }

        var motion = new Motion(board);
        Active.Add(element, motion);
        element.Unloaded += OnUnloaded;
        board.Completed += (_, _) =>
        {
            if (Active.TryGetValue(element, out var current) && ReferenceEquals(current, motion)) Stop(element);
        };

        board.Begin();
    }

    /// <summary>
    /// 进场用的位移 Transform：没有 RenderTransform 就装一支 TranslateTransform，已有 TranslateTransform
    /// 就接着用，别的 Transform（页面自己摆的缩放之类）不抢 —— 那一档只淡入、不上浮。
    /// </summary>
    private static TranslateTransform? DriftFor(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        if (element.RenderTransform is not null) return null;

        var drift = new TranslateTransform();
        element.RenderTransform = drift;
        return drift;
    }

    /// <summary>恢复最终画面；没有正在播放的动画时也必须保持不透明，尤其是离树的自检实例。</summary>
    internal static void Stop(FrameworkElement? element)
    {
        if (element is null) return;

        element.Unloaded -= OnUnloaded;
        if (Waiting.TryGetValue(element, out var handler))
        {
            element.Loaded -= handler;
            Waiting.Remove(element);
        }

        if (!Active.TryGetValue(element, out var motion))
        {
            element.Opacity = 1;
            return;
        }

        // 先摘下记录，Completed 引发的递归 Stop 便不会二次清理。Stop 释放 HoldEnd 的值，
        // 落点再用本地值写死一遍，反复 Stop 也幂等。
        Active.Remove(element);
        motion.Board.Stop();
        element.Opacity = 1;
        if (element.RenderTransform is TranslateTransform drift) drift.Y = 0;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Stop(element);
    }

    private sealed record Motion(Storyboard Board);

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
            if (_root.XamlRoot is null || args.Element is not FrameworkElement element) return;

            var target = TargetOf(element);
            if (target is not null) RevealWhenLoaded(target, Math.Min(args.Index, 5) * StaggerMs);
        }

        private static void OnClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        {
            if (args.Element is FrameworkElement element) Stop(TargetOf(element));
        }

        private void OnRootUnloaded(object sender, RoutedEventArgs e)
        {
            _root.Unloaded -= OnRootUnloaded;
            _repeater.ElementPrepared -= OnPrepared;
            _repeater.ElementClearing -= OnClearing;
            Shelves.Remove(_repeater);

            var count = _repeater.ItemsSourceView?.Count ?? 0;
            for (var index = 0; index < count; index++)
            {
                if (_repeater.TryGetElement(index) is not FrameworkElement element) continue;

                var target = TargetOf(element);
                if (target is not null) Stop(target);
            }
        }
    }
}
