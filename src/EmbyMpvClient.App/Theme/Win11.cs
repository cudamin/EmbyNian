using System.Runtime.InteropServices;

namespace EmbyMpvClient.App.Theme;

/// <summary>
/// The handful of Win11 window attributes worth setting from a WinForms app: a dark title bar,
/// rounded corners and a caption painted in the app's own colour.
/// <para>
/// Every call is optional and failure is ignored. DwmSetWindowAttribute returns an error for
/// attributes the running build does not know, which is exactly what should happen — the window
/// then just looks like a normal one.
/// </para>
/// </summary>
internal static partial class Win11
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int CornerPreference = 33;

    private const int CornerRound = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr window, string? subApp, string? subIdList);

    /// <summary>
    /// Asks the theme engine for the dark variant of a common control's chrome. The one thing this
    /// buys that colours cannot is a dark scroll bar: a scroll bar is drawn by the system, ignores
    /// <see cref="Control.BackColor"/> entirely, and otherwise sits on a dark page as a white strip.
    /// </summary>
    public static void UseDarkControls(Control control)
    {
        if (!control.IsHandleCreated)
        {
            control.HandleCreated += (_, _) => UseDarkControls(control);
            return;
        }

        try
        {
            // "DarkMode_Explorer" is the theme class the shell itself uses; unknown class names are
            // an error return rather than an exception, so an older build just keeps its light chrome.
            _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Applies the whole set to a form; safe to call before or after the handle exists.</summary>
    public static void Apply(Form form, bool rounded = true)
    {
        if (!form.IsHandleCreated)
        {
            // Attributes only stick once there is a window; re-apply when there is one.
            form.HandleCreated += (_, _) => Apply(form, rounded);
            return;
        }

        UseDarkTitleBar(form.Handle);
        SetCaptionColor(form.Handle, Palette.Window);
        SetBorderColor(form.Handle, Palette.Border);
        SetCaptionTextColor(form.Handle, Palette.TextDim);
        if (rounded) UseRoundedCorners(form.Handle);
    }

    public static void UseDarkTitleBar(IntPtr window) => Set(window, UseImmersiveDarkMode, 1);

    public static void UseRoundedCorners(IntPtr window) => Set(window, CornerPreference, CornerRound);

    public static void SetCaptionColor(IntPtr window, Color color) => Set(window, CaptionColor, ToBgr(color));

    public static void SetBorderColor(IntPtr window, Color color) => Set(window, BorderColor, ToBgr(color));

    public static void SetCaptionTextColor(IntPtr window, Color color) => Set(window, TextColor, ToBgr(color));

    /// <summary>DWM wants 0x00BBGGRR, which is the reverse of what Color.ToArgb gives.</summary>
    private static int ToBgr(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static void Set(IntPtr window, int attribute, int value)
    {
        if (window == IntPtr.Zero) return;

        try
        {
            _ = DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is present on every supported Windows; guarded anyway so a missing
            // system DLL degrades the chrome instead of failing the launch.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}

/// <summary>
/// The two window styles that make a form cover the whole screen without a border. Done with
/// SetWindowLongPtr instead of <see cref="Form.FormBorderStyle"/>: changing that property makes
/// WinForms recreate the form's handle, which would destroy the mpv child window rendering the
/// video. The styles are flipped in place instead.
/// </summary>
internal static partial class WindowChrome
{
    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Removes the caption and the resizing frame so the window spans the screen edge to edge.
    /// Setting <paramref name="captioned"/> back to true restores them.
    /// </summary>
    public static void SetCaption(Form form, bool captioned)
    {
        if (!form.IsHandleCreated) return;

        try
        {
            var style = GetWindowLongPtr(form.Handle, GwlStyle).ToInt64();
            if (captioned) style |= WsCaption | WsThickFrame;
            else style &= ~(WsCaption | WsThickFrame);

            _ = SetWindowLongPtr(form.Handle, GwlStyle, new IntPtr(style));
            // The 0-sized rect must stay untouched, or the frame change shrinks the window away.
            _ = SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
