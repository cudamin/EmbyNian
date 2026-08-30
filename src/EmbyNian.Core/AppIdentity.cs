namespace EmbyNian;

/// <summary>
/// What the program calls itself, read off the assembly so the version can never drift from the
/// build. Lives in Core because both shells need it during the WinUI 3 changeover.
/// </summary>
public static class AppIdentity
{
    /// <summary>
    /// The on-screen name. Deliberately not the same string as
    /// <see cref="Emby.DeviceIdentity.ClientName"/>: that one is how Emby's session list labels this
    /// client, and renaming it would make the server treat it as a different client entirely.
    /// </summary>
    public const string Title = "EmbyNian";

    public static string Version { get; } =
        typeof(AppIdentity).Assembly.GetName().Version?.ToString(3) ?? "3.0.0";

    public static string TitleWithVersion => $"{Title} {Version}";
}
