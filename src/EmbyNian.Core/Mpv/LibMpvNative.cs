using System.Runtime.InteropServices;

namespace EmbyNian.Mpv;

/// <summary>
/// The mpv client API surface this client needs, P/Invoked from libmpv-2.dll. The ABI is
/// declared stable by mpv, so the handful of structs below are safe to marshal by value.
/// Follows the same convention as the app's Theme/Win11.cs: one thin interop class, no
/// managed logic — every wrapper with behaviour lives next to it, not inside it.
/// </summary>
internal static class LibMpvNative
{
    private const string Library = "libmpv-2.dll";

    // ---- search path ------------------------------------------------------------

    /// <summary>LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: let LoadLibrary use AddDllDirectory additions.</summary>
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string directory);

    private static string? _libraryPath;
    private static int _resolverInstalled;

    /// <summary>
    /// Points every <c>libmpv-2.dll</c> import at one absolute file. Without this a configured
    /// path outside the program folder would be found by <see cref="File.Exists"/> and then fail
    /// to load, because a bare DllImport name is only ever resolved against the search
    /// directories — the settings' path would be validated and then ignored.
    /// </summary>
    internal static void UseLibrary(string path)
    {
        _libraryPath = path;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;

        NativeLibrary.SetDllImportResolver(typeof(LibMpvNative).Assembly, (name, _, _) =>
            string.Equals(name, Library, StringComparison.OrdinalIgnoreCase)
            && _libraryPath is { } resolved
            && NativeLibrary.TryLoad(resolved, out var handle)
                ? handle
                : IntPtr.Zero);
    }

    /// <summary>
    /// libmpv-2.dll depends on siblings like lua51.dll that live next to mpv.exe, not next to
    /// this client; without this, LoadLibrary fails with "找不到指定的模块" the moment the dll
    /// sits in a different folder. Best effort: on an old Windows the call just fails.
    /// </summary>
    internal static void EnsureDependencyDirectories(params string[] directories)
    {
        try
        {
            if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs)) return;
            foreach (var directory in directories)
            {
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    AddDllDirectory(directory);
            }
        }
        catch (Exception)
        {
            // Never take playback down over a search-path hint.
        }
    }

    // ---- lifecycle -------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    // ---- options and properties ------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    /// <summary>
    /// Sets an option with a typed value, for the few options (like <c>wid</c>) that must not
    /// go through string parsing.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref long data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    /// <summary>Returns a copy that must be released with <see cref="mpv_free"/>; null when unavailable.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>
    /// Writes a property as an mpv node into the memory <paramref name="data"/> points at; the
    /// node's contents must then be released with <see cref="mpv_free_node_contents"/>. Used for
    /// structured properties like track-list, which have no string form.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, IntPtr data);

    /// <summary>
    /// <c>mpv_get_property</c> with MPV_FORMAT_DOUBLE. A typed read costs no allocation and no
    /// parse, which matters for the properties the seek bar reads on every frame.
    /// </summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_double(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    /// <summary><c>mpv_get_property</c> with MPV_FORMAT_FLAG, whose C type is a 4-byte int.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_flag(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free_node_contents(IntPtr node);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(IntPtr context, ulong replyUserData, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_request_log_messages(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    // ---- commands --------------------------------------------------------------

    /// <summary>Args must be a null-terminated array of UTF-8 strings.</summary>
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr context, IntPtr[] args);

    // ---- event loop -------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr context, double timeout);

    // ---- formats and event ids --------------------------------------------------

    /// <summary>
    /// MPV_FORMAT_NONE. As an observe format it means "tell me it changed, not what to"; the
    /// value is then read on the event thread, which is the only way to take a structured
    /// property like track-list, whose node tree has to be walked and freed in one place.
    /// </summary>
    internal const int FormatNone = 0;

    /// <summary>MPV_FORMAT_STRING: text property values.</summary>
    internal const int FormatString = 1;

    /// <summary>MPV_FORMAT_FLAG: boolean property values.</summary>
    internal const int FormatFlag = 3;

    /// <summary>MPV_FORMAT_INT64: integer property values.</summary>
    internal const int FormatInt64 = 4;

    /// <summary>MPV_FORMAT_DOUBLE: floating point property values.</summary>
    internal const int FormatDouble = 5;

    /// <summary>MPV_FORMAT_NODE: any value, as a tree of <see cref="MpvNode"/>.</summary>
    internal const int FormatNode = 6;

    /// <summary>MPV_FORMAT_NODE_ARRAY.</summary>
    internal const int FormatNodeArray = 7;

    /// <summary>MPV_FORMAT_NODE_MAP.</summary>
    internal const int FormatNodeMap = 8;

    internal const int EventShutdown = 1;
    internal const int EventLogMessage = 2;

    /// <summary>MPV_EVENT_START_FILE: a new file is being opened.</summary>
    internal const int EventStartFile = 6;

    internal const int EventEndFile = 7;

    /// <summary>MPV_EVENT_FILE_LOADED: the tracks and the duration are known from here on.</summary>
    internal const int EventFileLoaded = 8;

    /// <summary>MPV_EVENT_VIDEO_RECONFIG: the video output changed size or format.</summary>
    internal const int EventVideoReconfig = 17;

    /// <summary>MPV_EVENT_SEEK: a seek was started; time-pos is unreliable until PlaybackRestart.</summary>
    internal const int EventSeek = 20;

    /// <summary>MPV_EVENT_PLAYBACK_RESTART: playback resumed after a seek or after buffering.</summary>
    internal const int EventPlaybackRestart = 21;

    internal const int EventPropertyChange = 22;

    /// <summary>MPV_END_FILE_REASON_EOF / STOP / QUIT / ERROR / REDIRECT.</summary>
    internal const int EndFileEof = 0;
    internal const int EndFileStop = 2;
    internal const int EndFileQuit = 3;
    internal const int EndFileError = 4;
    internal const int EndFileRedirect = 5;

    // ---- event payloads ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEvent
    {
        internal int EventId;
        internal int Error;
        internal ulong ReplyUserData;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventEndFile
    {
        internal int Reason;
        internal int Error;
        internal ulong PlaylistEntryId;
        internal int PlaylistInsertId;
        internal int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventProperty
    {
        internal IntPtr Name;
        internal int Format;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvEventLogMessage
    {
        internal IntPtr Prefix;
        internal IntPtr Level;
        internal IntPtr Text;
        internal int LogLevel;
    }

    /// <summary>
    /// One mpv node: an 8-byte union plus the format that says which member it holds.
    /// On x64 the union is 8 bytes (a double or a pointer), so the whole struct is 16 with padding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNode
    {
        internal IntPtr Union;
        internal int Format;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MpvNodeList
    {
        internal int Num;
        internal IntPtr Values;
        internal IntPtr Keys;
    }
}