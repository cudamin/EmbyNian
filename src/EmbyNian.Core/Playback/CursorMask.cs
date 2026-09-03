namespace EmbyNian.Playback;

/// <summary>
/// 一只「什么都不画」的鼠标光标，它的两张掩码长什么样。
/// <para>
/// 这是「静止两秒鼠标要自动隐藏」这件事里唯一有唯一正确答案、也唯一能被单测钉住的计算。它值得下沉，不是因为
/// 规矩要求，而是因为**从这次起框架会照着这张掩码画光标**：以前这只透明光标只用来喂 <c>SetCursor</c> 和窗口
/// 类，掩码算错的下场是「藏不掉」；现在它还要交给 WinUI 的输入管线，算错的下场是画面正中多出一块黑方块。
/// </para>
/// <para>
/// Win32 的光标由两张单色位图叠出来（<c>CreateCursor</c> 的 AND 和 XOR）。逐像素的规矩是：
/// AND=1、XOR=0 → 透明；AND=0、XOR=0 → 黑；AND=0、XOR=1 → 白；AND=1、XOR=1 → 反色。所以「整只都透明」就是
/// **AND 全 1、XOR 全 0**。
/// </para>
/// </summary>
public static class CursorMask
{
    /// <summary>
    /// 一行单色位图占几个字节。Win32 的单色位图行距按 <b>WORD</b>（2 字节）对齐，不是按字节 —— 33 像素宽的一行
    /// 占 6 个字节而不是 5。这一条最容易写错，而错了不是「差一点」：整张掩码会逐行错位，画出来是一块斜纹。
    /// </summary>
    public static int Stride(int width) => width <= 0 ? 0 : (width + 15) / 16 * 2;

    /// <summary>
    /// 一只 <paramref name="width"/>×<paramref name="height"/> 的全透明光标的两张掩码。
    /// <para>
    /// 宽或高不是正数就两张都返回空数组 —— 调用方据此不建光标、退回「没有透明光标可用」那条路。抛异常不行：
    /// 那条路会把「藏不掉鼠标」升级成「播放器起不来」。
    /// </para>
    /// </summary>
    public static (byte[] And, byte[] Xor) Transparent(int width, int height)
    {
        if (width <= 0 || height <= 0) return ([], []);

        var bytes = Stride(width) * height;
        var and = new byte[bytes];

        Array.Fill(and, (byte)0xFF);

        return (and, new byte[bytes]);
    }
}
