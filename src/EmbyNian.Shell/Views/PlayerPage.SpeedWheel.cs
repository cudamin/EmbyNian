using System.ComponentModel;
using System.Globalization;
using EmbyNian.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using EmbyNian.Shell.ViewModels;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 倍速轮盘（2026-09-25 用户令：「集成模式下的播放速度按钮，改为竖置的滚动条滚轮，刻度居中，使用鼠标
/// 滚轮翻动或者鼠标左键长按拖拽」）。
/// <para>
/// 算术全在 Core 的 <see cref="SpeedWheel"/>（位置↔速度、吸附、一步、摆位与浓淡，测试钉着方向与端点），
/// 这里只做三件事：<see cref="BuildSpeedWheel"/> 把刻度摆上带子；两个手势 —— 滚轮一格拨一根、左键长按
/// 拖拽内容跟手 —— 把手上的动作翻译成带子上的连续位置；<see cref="ApplyWheelCrossing"/> 在中线跨过某根
/// 刻度时把那一档交给 <see cref="PlayerViewModel.SetSpeed"/>（与音量条同一套「跨过整格才动数值」的账法，
/// 只是这里没有要攒的半格）。
/// </para>
/// <para>
/// 两条刻意为之的规则：<b>打开时不吸附</b> —— 键盘微调（±0.1）给出的表外速度停在两根刻度之间，吸附了
/// 就是按钮写一个数、片子里跑另一个数；<b>自己下的单不追自己的回声</b> —— 轮盘开着时键盘那几路改速度
/// 轮盘要跟过去，但拖拽与吸附动画进行中的 mpv 回声不许把带子从手上拽走。
/// </para>
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>一根刻度的高（逻辑像素）。中线两侧各留出半根，视觉上「一格一格」靠的就是这个盒子。</summary>
    private const double WheelTickHeight = 36;

    /// <summary>拨一格 / 松手吸附那一趟的时长。逐拍走 16ms 计时器（cubic-out），与转场同一笔法 ——
    /// SpriteVisual 的变换用 Storyboard 是静默不跑的老坑，这里虽然只是 XAML 元素，也照走计时器。</summary>
    private const int WheelSettleMilliseconds = 120;

    private TextBlock[]? _wheelTicks;

    /// <summary>刻度带此刻的连续位置（0 = 最慢一档、count-1 = 最快）。含义与换算见 <see cref="SpeedWheel"/>。</summary>
    private double _wheelPosition;

    /// <summary>轮盘自己最近一次交给 mpv 的速度。NaN = 还没交过（刚打开）；与中线那一档对不上才下发，
    /// 拖着带子扫过五档就是五次 <c>SetSpeed</c>，停在原地一档也不多发。</summary>
    private double _wheelAppliedSpeed = double.NaN;

    /// <summary>左键长按拖拽的账：进行中、起点在哪（像素与位置各记一份）。</summary>
    private bool _wheelDragging;

    private double _wheelDragStartY;

    private double _wheelDragStartPosition;

    /// <summary>拨动／吸附那一趟的逐拍轨道。懒创建 —— 轮盘不开就不占一颗表。</summary>
    private DispatcherQueueTimer? _wheelTimer;

    private double _wheelFrom;

    private double _wheelTo;

    private long _wheelStartedAt;

    private bool _wheelAnimating;

    /// <summary>接线与刻度的第一次摆放。构造器调用一次；刻度挂在 Flyout 的内容树里，不上屏也在树上。</summary>
    private void WireSpeedWheel()
    {
        BuildSpeedWheel();

        SpeedWheelPanel.PointerPressed += OnSpeedWheelPanelPressed;
        SpeedWheelPanel.PointerMoved += OnSpeedWheelPanelMoved;
        SpeedWheelPanel.PointerReleased += OnSpeedWheelPanelReleased;
        SpeedWheelPanel.PointerCaptureLost += OnSpeedWheelPanelCaptureLost;
        SpeedWheelPanel.PointerWheelChanged += OnSpeedWheelPanelWheel;
    }

    /// <summary>一根刻度上的文案。与倍速菜单同一套写法（<c>0.0#</c>），「正常」两个字省给按钮标签去说 ——
    /// 刻度带要的是扫一眼能对上号，不是解释。</summary>
    internal static string WheelTickText(double rate) =>
        $"{rate.ToString("0.0#", CultureInfo.InvariantCulture)}×";

    /// <summary>
    /// 刻度表上的每一根都摆上带子（根数跟着 <see cref="PlayerViewModel.SpeedChoices"/> 走）。摆位
    /// （Canvas.Top）由 <see cref="RenderWheel"/> 逐拍写，这里只负责把它们造出来；
    /// 幂等 —— 自检探针随时可以再来一趟。
    /// </summary>
    private void BuildSpeedWheel()
    {
        if (_wheelTicks is not null) return;

        var choices = PlayerViewModel.SpeedChoices;
        var width = SpeedWheelPanel.Width;
        _wheelTicks = new TextBlock[choices.Length];

        for (var index = 0; index < choices.Length; index++)
        {
            var tick = new TextBlock
            {
                Text = WheelTickText(choices[index]),
                Width = width,
                TextAlignment = TextAlignment.Center,
                FontSize = 14,
                Foreground = (Brush)Resources["PlayerInkBrush"],
                IsHitTestVisible = false,
                RenderTransform = new ScaleTransform
                {
                    CenterX = width / 2,
                    CenterY = WheelTickHeight / 2
                }
            };

            // 36 高的盒子让 14px 的字垂直居中：内边距 = 高度减一行字再对半。字号改动只动这一处。
            tick.Padding = new Thickness(0, (WheelTickHeight - tick.FontSize * 1.34) / 2, 0, 0);

            Canvas.SetLeft(tick, 0);
            SpeedWheelTrack.Children.Add(tick);
            _wheelTicks[index] = tick;
        }
    }

    /// <summary>按当前 <see cref="_wheelPosition"/> 摆一遍刻度。每一句赋值都带值比较，静止时一个像素都不碰。</summary>
    private void RenderWheel()
    {
        if (_wheelTicks is null) return;

        var center = SpeedWheelPanel.Height / 2;

        for (var index = 0; index < _wheelTicks.Length; index++)
        {
            var offset = SpeedWheel.TickOffset(index, _wheelPosition);
            var distance = Math.Abs(offset) / SpeedWheel.TickSpacing;
            var tick = _wheelTicks[index];

            var top = center - WheelTickHeight / 2 + offset;
            // 首摆时 GetTop 是 NaN：NaN 参与的值比较恒为假，会把第一次摆放整个吞掉 —— 必须单判。
            if (double.IsNaN(Canvas.GetTop(tick)) || Math.Abs(Canvas.GetTop(tick) - top) > 0.01)
                Canvas.SetTop(tick, top);

            var opacity = SpeedWheel.TickOpacity(distance);
            if (Math.Abs(tick.Opacity - opacity) > 0.001) tick.Opacity = opacity;

            var emphasis = SpeedWheel.TickScale(distance);
            if (tick.RenderTransform is ScaleTransform scale && Math.Abs(scale.ScaleX - emphasis) > 0.001)
            {
                scale.ScaleX = emphasis;
                scale.ScaleY = emphasis;
            }
        }
    }

    // ---- 打开与关闭 ----------------------------------------------------------------

    private void OnSpeedWheelOpened(object sender, object e)
    {
        if (!Attached) return;

        StopWheelAnimation();
        _wheelDragging = false;

        // 打开时停在与当前速度对应的连续位置上，不吸附 —— 理由见类注释。第一次交单之前 applied 记着
        // 现速：用户只是开开看看又关上，一格都不该多发。
        var choices = PlayerViewModel.SpeedChoices;
        _wheelPosition = SpeedWheel.PositionFor(ViewModel.Status.Speed, choices);
        _wheelAppliedSpeed = ViewModel.Status.Speed;
        RenderWheel();

        // 轮盘开着的时候别的路（键盘微调、恢复常速）改了速度，带子跟过去。
        ViewModel.PropertyChanged += OnSpeedWheelViewModelChanged;
    }

    private void OnSpeedWheelClosed(object sender, object e)
    {
        ViewModel.PropertyChanged -= OnSpeedWheelViewModelChanged;
        StopWheelAnimation();
        _wheelDragging = false;
    }

    private void OnSpeedWheelViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.SpeedLabel)) return;
        if (!Attached || _wheelTicks is null || _wheelDragging || _wheelAnimating) return;

        // 别人的回声与自己下的单在这里分不开，也不必分：位置本来就停在那一档上，重摆是空操作；
        // 真被别的路改了（键盘那一对），带子这才跟着走。
        var choices = PlayerViewModel.SpeedChoices;
        _wheelPosition = SpeedWheel.PositionFor(ViewModel.Status.Speed, choices);
        _wheelAppliedSpeed = ViewModel.Status.Speed;
        RenderWheel();
    }

    // ---- 手势一：滚轮翻动 ------------------------------------------------------------

    private void OnSpeedWheelPanelWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(SpeedWheelPanel).Properties.MouseWheelDelta;
        e.Handled = true;

        if (delta == 0 || !Attached || _wheelTicks is null || _wheelDragging) return;

        // 滚轮向上（delta > 0）= 露出上面更快的那几根，与音量条同一个心智模型（往上 = 更多）。
        // 高分辨率滚轮一次报多格也只走一根 —— 带子有吸附动画垫着，连拨的手感靠它续。
        AnimateWheelTo(SpeedWheel.Stepped(_wheelPosition, PlayerViewModel.SpeedChoices.Length, delta));
    }

    // ---- 手势二：左键长按拖拽 ---------------------------------------------------------

    private void OnSpeedWheelPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!Attached || _wheelTicks is null) return;

        var point = e.GetCurrentPoint(SpeedWheelPanel);
        if (!point.Properties.IsLeftButtonPressed) return;

        // 只认左键（用户令「鼠标左键长按拖拽」）：右键、笔杆键落在这里都不是拖拽。
        StopWheelAnimation();
        _wheelDragging = true;
        _wheelDragStartY = point.Position.Y;
        _wheelDragStartPosition = _wheelPosition;
        SpeedWheelPanel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSpeedWheelPanelMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_wheelDragging || !Attached || _wheelTicks is null) return;

        var y = e.GetCurrentPoint(SpeedWheelPanel).Position.Y;
        var choices = PlayerViewModel.SpeedChoices;

        // 内容跟手指走（滚轮盘的通用手势）：手往下带，带子往下走，从上面转进来的是更快的那几根。
        // 算式与方向判据同住 Core 的 TickOffset，这里不再抄一份带方向的式子。
        var moved = _wheelDragStartPosition + (y - _wheelDragStartY) / SpeedWheel.TickSpacing;
        var clamped = Math.Clamp(moved, 0, choices.Length - 1);
        if (Math.Abs(clamped - _wheelPosition) < 0.0005) return;

        _wheelPosition = clamped;
        RenderWheel();
        ApplyWheelCrossing();
        e.Handled = true;
    }

    private void OnSpeedWheelPanelReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_wheelDragging) return;

        EndWheelDrag(e.Pointer);
        e.Handled = true;
    }

    /// <summary>拿走捕获的那条路（拖到窗外松手、被系统抢走）。松手与丢捕获殊途同归：吸附最近刻度。</summary>
    private void OnSpeedWheelPanelCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_wheelDragging) return;

        EndWheelDrag(null);
    }

    private void EndWheelDrag(Pointer? pointer)
    {
        _wheelDragging = false;
        if (pointer is not null) SpeedWheelPanel.ReleasePointerCapture(pointer);

        AnimateWheelTo(SpeedWheel.Snap(_wheelPosition, PlayerViewModel.SpeedChoices.Length));
    }

    // ---- 交单与那一趟吸附动画 ---------------------------------------------------------

    /// <summary>中线跨过刻度 = 换档：把中线那一档交给 mpv。没跨过（相邻两次问的同一根）就什么都不发。</summary>
    private void ApplyWheelCrossing()
    {
        if (!Attached) return;

        var choices = PlayerViewModel.SpeedChoices;
        var speed = choices[SpeedWheel.Snap(_wheelPosition, choices.Length)];
        if (!double.IsNaN(_wheelAppliedSpeed) && Math.Abs(speed - _wheelAppliedSpeed) <= 0.001) return;

        _wheelAppliedSpeed = speed;
        ViewModel.SetSpeed(speed, notice: false);
    }

    /// <summary>把带子送到 <paramref name="targetIndex"/>：差半格以上起一趟 cubic-out，原地就只交单。</summary>
    private void AnimateWheelTo(int targetIndex)
    {
        _wheelFrom = _wheelPosition;
        _wheelTo = targetIndex;

        if (Math.Abs(_wheelTo - _wheelFrom) < 0.001)
        {
            _wheelPosition = _wheelTo;
            RenderWheel();
            ApplyWheelCrossing();
            return;
        }

        _wheelAnimating = true;
        _wheelStartedAt = Now;
        WheelTimer().Start();
    }

    private void AdvanceWheel()
    {
        var progress = Math.Clamp((Now - _wheelStartedAt) / (double)WheelSettleMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);

        _wheelPosition = _wheelFrom + (_wheelTo - _wheelFrom) * eased;
        RenderWheel();
        ApplyWheelCrossing();

        if (progress < 1) return;

        _wheelPosition = _wheelTo;
        _wheelAnimating = false;
        WheelTimer().Stop();
        RenderWheel();
        ApplyWheelCrossing();
    }

    private void StopWheelAnimation()
    {
        if (_wheelTimer?.IsRunning == true) _wheelTimer.Stop();
        _wheelAnimating = false;
    }

    private DispatcherQueueTimer WheelTimer()
    {
        if (_wheelTimer is not null) return _wheelTimer;

        _wheelTimer = DispatcherQueue.CreateTimer();
        _wheelTimer.Interval = TimeSpan.FromMilliseconds(16);
        _wheelTimer.Tick += (_, _) => AdvanceWheel();
        return _wheelTimer;
    }
}
