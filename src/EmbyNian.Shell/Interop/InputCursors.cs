using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace EmbyNian.Shell.Interop;

/// <summary>
/// 把一只 Win32 的 <c>HCURSOR</c> 包成框架自己的 <see cref="InputCursor"/>。
/// <para>
/// <b>为什么非要这一层</b>：指针压在 XAML 内容上的时候，屏上那个光标由 WinUI 的输入管线说了算 —— 我们在界面
/// 线程上做的 <c>SetCursor</c>、<c>ShowCursor</c>、换窗口类的光标，它一概不看。真片子的日志把这件
/// 事写得很清楚：一次藏了两分零五秒，这条队列全程没有形状、显示计数 −1、五个窗口类都换成透明、催过一次，而
/// <c>GetCursorInfo</c> 一路答系统箭头 0x10003。要动框架画的那只，只有 <c>UIElement.ProtectedCursor</c> 一条路。
/// </para>
/// <para>
/// 而 <see cref="InputSystemCursor"/> 没有「无」这一档：它只列得出箭头、手形、等待那十几种形状。框架里唯一能把
/// 「一只自己造的光标」交进去的入口是 <c>IInputCursorStaticsInterop::CreateFromHCursor</c>（IID
/// <c>ac6f5065-90c4-46ce-beb7-05e138e54117</c>，声明在 Windows App SDK 随包发的
/// <c>Microsoft.UI.Input.InputCursor.Interop.h</c> 里），而它没有 C# 投影，只能手搓一次 vtable 调用 —— 写法照
/// <see cref="Native.MarkFullscreen"/> 那一处，那儿已经在这么干了。
/// </para>
/// <para>
/// 另一条路（在 exe 里嵌一个 <c>.cur</c> 资源再用 <c>InputDesktopResourceCursor.CreateFromModuleFile</c>）刻意
/// 不走：那要用 <c>&lt;Win32Resource&gt;</c> 顶掉 SDK 自动生成的那份 Win32 资源，而那份资源里装着刚磨过圆角的
/// 应用图标和整份 WinAppSDK 的 activatableClass 清单。风险高一个量级，换来的是同一件事。
/// </para>
/// </summary>
internal static partial class InputCursors
{
    /// <summary><c>Microsoft.UI.Input.InputCursor</c> 的运行时类名，工厂按它取。</summary>
    private const string InputCursorClass = "Microsoft.UI.Input.InputCursor";

    private static readonly Guid StaticsInterop = new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    /// <summary>
    /// 把 <paramref name="cursor"/> 包成框架的 <see cref="InputCursor"/>，包不出来就返回 null。
    /// <para>
    /// 每一步都可能失败（工厂取不到、接口不认、返回空指针），而失败的正确后果是<b>退回今天的行为</b> —— 光标照旧
    /// 藏不掉，但程序照常跑。所以整段包在 try 里、一律 return null，由调用方记一条警告，自检那条「透明光标包成
    /// 框架的了」当场判红：失败要是看得见的，不要是静默的。
    /// </para>
    /// <para>
    /// <b>拿到的 ABI 指针故意不 Release。</b>这只光标整个进程只建一次、一直留到退出，少一次释放换来的是「包装到
    /// 底 AddRef 了没有」这个问题永远不用赌。同一个理由让 <c>HostWindow.Dispose</c> 在包过之后不再
    /// <c>DestroyCursor</c>：框架手里还捏着那个句柄。
    /// </para>
    /// </summary>
    internal static InputCursor? From(IntPtr cursor)
    {
        if (cursor == IntPtr.Zero) return null;

        var name = IntPtr.Zero;
        var factory = IntPtr.Zero;

        try
        {
            if (WindowsCreateString(InputCursorClass, InputCursorClass.Length, out name) < 0) return null;

            var interop = StaticsInterop;
            if (RoGetActivationFactory(name, ref interop, out factory) < 0 || factory == IntPtr.Zero) return null;

            // 槽位 0-2 是 IUnknown，3-5 是 IInspectable，6 就是这个接口唯一的方法。
            var vtable = Marshal.ReadIntPtr(factory);
            var create = Marshal.GetDelegateForFunctionPointer<CreateFromHCursor>(
                Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));

            if (create(factory, cursor, out var abi) < 0 || abi == IntPtr.Zero) return null;

            return WinRT.MarshalInspectable<InputCursor>.FromAbi(abi);
        }
        catch (Exception)
        {
            // 一个答得出工厂却不是这个接口的运行时，不值得让播放器起不来。
            return null;
        }
        finally
        {
            if (name != IntPtr.Zero) WindowsDeleteString(name);

            if (factory != IntPtr.Zero)
            {
                var release = Marshal.GetDelegateForFunctionPointer<ComRelease>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 2 * IntPtr.Size));
                release(factory);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFromHCursor(IntPtr self, IntPtr cursor, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ComRelease(IntPtr self);

    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out IntPtr handle);

    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    private static partial int WindowsDeleteString(IntPtr handle);

    [LibraryImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static partial int RoGetActivationFactory(IntPtr className, ref Guid interfaceId, out IntPtr factory);
}
