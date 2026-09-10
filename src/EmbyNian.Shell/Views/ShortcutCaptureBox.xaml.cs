using EmbyNian.Playback;
using EmbyNian.Shell.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 抓一个键盘组合键的方框（见 XAML 头部）。往外说的只有一件事：<see cref="Committed"/> —— 用户敲定了一个组合键
/// （token 串，见 <see cref="ShortcutCatalog.Serialize"/>），或按 × 清除（空串）。屏上显示什么由 <see cref="Combo"/>
/// 绑进来（VM 用 <see cref="ShortcutCatalog.Format"/> 算好的 display 串）；抓键那一下不改 <see cref="Combo"/>，
/// 由 VM 决定成不成（冲突就拦下），成了才回头把新的 display 串推回来，所以没有两Way回声那套麻烦。
/// </summary>
public sealed partial class ShortcutCaptureBox : UserControl
{
    private bool _capturing;

    public ShortcutCaptureBox()
    {
        InitializeComponent();

        ComboBorder.PointerPressed += (_, _) => StartCapture();
        ClearButton.Click += (_, _) =>
        {
            if (_capturing) EndCapture();
            Committed?.Invoke(this, "");
        };
        KeyDown += OnKeyDown;
        LostFocus += (_, _) => { if (_capturing) EndCapture(); };

        UpdateDisplay();
    }

    public static readonly DependencyProperty ComboProperty = DependencyProperty.Register(
        nameof(Combo), typeof(string), typeof(ShortcutCaptureBox), new PropertyMetadata("", OnComboChanged));

    /// <summary>屏上显示的那串。VM 用 <see cref="ShortcutCatalog.Format"/> 算好塞进来，本控件只显示、不解析。</summary>
    public string Combo
    {
        get => (string)GetValue(ComboProperty);
        set => SetValue(ComboProperty, value);
    }

    private static void OnComboChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        // 抓键中不跟外面的更新走 —— 屏上正显示着「按快捷键…」，等这一轮抓完自己会刷。
        var box = (ShortcutCaptureBox)sender;
        if (!box._capturing) box.UpdateDisplay();
    }

    /// <summary>用户敲定了一个组合键（token）或按 × 清除（空串）。绑不绑得上由听者（VM）判，可能被拦下。</summary>
    public event EventHandler<string>? Committed;

    private void StartCapture()
    {
        _capturing = true;
        Focus(FocusState.Pointer);
        UpdateDisplay();
    }

    private void EndCapture()
    {
        _capturing = false;
        UpdateDisplay();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing)
        {
            // 键盘用户按空格/回车「进入捕获」，和点一下方框一个意思。别的键放过去 —— 方向键还要在设置列表里
            // 导航。要绑空格本身，就是进了捕获之后再按一下空格（那一下才被抓）。
            if (e.Key is VirtualKey.Space or VirtualKey.Enter)
            {
                StartCapture();
                e.Handled = true;
            }

            return;
        }

        // Esc 取消（它本来就是保留键、绑不了，正好当取消）；Tab 取消并放行，让焦点照常往下走。
        if (e.Key == VirtualKey.Escape) { EndCapture(); e.Handled = true; return; }
        if (e.Key == VirtualKey.Tab) { EndCapture(); return; }

        // 只按下修饰键先不算 —— 等一个真正的键。
        if (IsModifier(e.Key)) { e.Handled = true; return; }

        e.Handled = true;

        if (KeyStrokeInterop.Token(e.Key) is not { } token) return; // 认不出的键，忽略、继续等

        var stroke = new KeyStroke(token, Native.CtrlHeld, Native.AltHeld, Native.ShiftHeld);

        if (ShortcutCatalog.IsReserved(stroke))
        {
            // Y 这类保留键：不收，留在捕获里、把话说破，等一个别的键。（Esc 上面已经当取消处理了。）
            ComboLabel.Text = "该键已保留（Esc / Y）";
            return;
        }

        _capturing = false;
        Committed?.Invoke(this, ShortcutCatalog.Serialize(stroke));
        UpdateDisplay();
    }

    private static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private void UpdateDisplay()
    {
        ComboLabel.Text = _capturing ? "按快捷键…" : (string.IsNullOrEmpty(Combo) ? "未设置" : Combo);
        VisualStateManager.GoToState(this, _capturing ? "Capturing" : "Normal", true);
    }
}
