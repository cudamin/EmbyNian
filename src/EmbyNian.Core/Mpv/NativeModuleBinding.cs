namespace EmbyNian.Mpv;

/// <summary>P/Invoke caches entry points individually, so a process must never mix mpv modules.</summary>
internal sealed class NativeModuleBinding
{
    private readonly object _gate = new();
    private string? _path;
    private IntPtr _module;

    internal void Select(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_module != IntPtr.Zero && !string.Equals(_path, fullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("内置播放器已绑定另一份原生库，请重启应用后切换");
            _path = fullPath;
        }
    }

    internal IntPtr Resolve(Func<string, IntPtr> load)
    {
        lock (_gate)
        {
            if (_module != IntPtr.Zero) return _module;
            var module = load(_path ?? throw new DllNotFoundException("尚未指定 libmpv"));
            if (module == IntPtr.Zero) throw new DllNotFoundException("无法加载指定的 libmpv");
            _module = module;
            return module;
        }
    }
}
