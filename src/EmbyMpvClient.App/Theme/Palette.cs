namespace EmbyMpvClient.App.Theme;

/// <summary>
/// Every colour, font and icon the UI uses. Centralised so the whole app changes together and
/// no control invents its own grey.
/// <para>
/// Dark by default: this is a client whose job is to hand a video to mpv, usually in a dim room,
/// and the poster art reads better against a dark surface.
/// </para>
/// </summary>
public static class Palette
{
    // ---- surfaces -------------------------------------------------------------
    public static readonly Color Window = Color.FromArgb(0x16, 0x18, 0x1C);
    public static readonly Color Surface = Color.FromArgb(0x1E, 0x21, 0x26);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x26, 0x2A, 0x31);
    public static readonly Color SurfaceHover = Color.FromArgb(0x2E, 0x33, 0x3B);
    public static readonly Color Border = Color.FromArgb(0x33, 0x38, 0x3F);
    public static readonly Color BorderStrong = Color.FromArgb(0x45, 0x4B, 0x54);

    // ---- text -----------------------------------------------------------------
    public static readonly Color Text = Color.FromArgb(0xF2, 0xF4, 0xF7);
    public static readonly Color TextDim = Color.FromArgb(0xA5, 0xAD, 0xBA);
    public static readonly Color TextFaint = Color.FromArgb(0x6F, 0x77, 0x83);
    public static readonly Color TextOnAccent = Color.FromArgb(0x10, 0x14, 0x12);

    // ---- accents --------------------------------------------------------------
    /// <summary>Emby's green, so the client looks like it belongs to the server it talks to.</summary>
    public static readonly Color Accent = Color.FromArgb(0x52, 0xB5, 0x4B);
    public static readonly Color AccentHover = Color.FromArgb(0x62, 0xC7, 0x5A);
    public static readonly Color AccentPressed = Color.FromArgb(0x42, 0x9B, 0x3C);
    public static readonly Color AccentSoft = Color.FromArgb(0x24, 0x3A, 0x27);

    public static readonly Color Danger = Color.FromArgb(0xE5, 0x53, 0x4B);
    public static readonly Color Warning = Color.FromArgb(0xE3, 0xB3, 0x41);
    public static readonly Color Info = Color.FromArgb(0x53, 0x9B, 0xF5);

    /// <summary>Scrim behind dialogs and the loading overlay.</summary>
    public static readonly Color Scrim = Color.FromArgb(0xB4, 0x0C, 0x0E, 0x11);

    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            from.A + (int)((to.A - from.A) * amount),
            from.R + (int)((to.R - from.R) * amount),
            from.G + (int)((to.G - from.G) * amount),
            from.B + (int)((to.B - from.B) * amount));
    }
}

/// <summary>
/// The app's fonts. Created once and kept for the process lifetime: fonts are handles, and a
/// grid that built a new one per cell would leak GDI objects until the paint stopped working.
/// </summary>
public static class Fonts
{
    private const string UiFamily = "Microsoft YaHei UI";

    /// <summary>
    /// Win11 ships Segoe Fluent Icons; Segoe MDL2 Assets is the Win10 name and carries the
    /// same codepoints for everything <see cref="Glyphs"/> uses.
    /// </summary>
    private static readonly string IconFamily = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets") ?? UiFamily;

    public static readonly Font Small = new(UiFamily, 8.25f);
    public static readonly Font Body = new(UiFamily, 9f);
    public static readonly Font BodyStrong = new(UiFamily, 9f, FontStyle.Bold);
    public static readonly Font Subtitle = new(UiFamily, 11f);
    public static readonly Font Title = new(UiFamily, 14f, FontStyle.Bold);
    public static readonly Font Display = new(UiFamily, 20f, FontStyle.Bold);
    public static readonly Font Mono = new(FirstInstalled("Cascadia Mono", "Consolas") ?? "Courier New", 9.5f);

    public static readonly Font Icon = new(IconFamily, 12f);
    public static readonly Font IconSmall = new(IconFamily, 8f);
    public static readonly Font IconLarge = new(IconFamily, 17f);
    public static readonly Font IconHuge = new(IconFamily, 24f);

    public static Font IconAt(float size) => new(IconFamily, size);

    private static string? FirstInstalled(params string[] families)
    {
        foreach (var family in families)
        {
            try
            {
                using var probe = new FontFamily(family);
                return family;
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }

        return null;
    }
}

/// <summary>
/// Segoe Fluent Icons / MDL2 codepoints. Written as escapes rather than literal characters:
/// they sit in the private use area, so a literal shows up as an empty box in most editors and
/// one careless copy-paste turns it into the wrong icon.
/// </summary>
public static class Glyphs
{
    public const string Home = "\uE80F";
    public const string Library = "\uE8F1";
    public const string Search = "\uE721";
    public const string Settings = "\uE713";
    public const string Edit = "\uE70F";
    public const string Play = "\uE768";
    public const string Pause = "\uE769";
    public const string Stop = "\uE71A";
    public const string Refresh = "\uE72C";
    public const string Back = "\uE72B";
    public const string ChevronLeft = "\uE76B";
    public const string ChevronRight = "\uE76C";
    public const string ChevronDown = "\uE70D";
    public const string ChevronUp = "\uE70E";
    public const string Check = "\uE73E";
    public const string Heart = "\uEB51";
    public const string HeartFilled = "\uEB52";
    public const string Warning = "\uE7BA";
    public const string Info = "\uE946";
    public const string Close = "\uE711";
    public const string Person = "\uE77B";
    public const string Lock = "\uE72E";
    public const string Globe = "\uE774";
    public const string Folder = "\uE8B7";
    public const string Save = "\uE74E";
    public const string Delete = "\uE74D";
    public const string Add = "\uE710";
    public const string Movie = "\uE8B2";
    public const string Series = "\uE7F4";
    public const string Music = "\uE8D6";
    public const string Photo = "\uEB9F";
    public const string OpenExternal = "\uE8A7";
    public const string Shader = "\uE81E";
    public const string Log = "\uE7C3";
    public const string SignOut = "\uE7E8";
    public const string Unwatched = "\uE739";
    public const string Fullscreen = "\uE740";
    public const string ExitFullscreen = "\uE73F";
    public const string Volume = "\uE767";
    public const string Mute = "\uE74F";
    public const string Previous = "\uE892";
    public const string Next = "\uE893";
    public const string List = "\uE71C";
    public const string Subtitles = "\uE8CB";
}
