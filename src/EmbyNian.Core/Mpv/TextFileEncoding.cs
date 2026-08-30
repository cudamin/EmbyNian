using System.Text;

namespace EmbyNian.Mpv;

/// <summary>
/// 文件原本使用的文字编码。配置编辑器写回时沿用它，避免编辑一个旧的 GBK 文件后中文注释变乱码。
/// </summary>
public sealed record TextFileEncoding(Encoding Encoding, bool HasByteOrderMark, string Description)
{
    private static readonly object Gate = new();
    private static bool _providerRegistered;

    public static TextFileEncoding Utf8NoBom { get; } = new(new UTF8Encoding(false), false, "UTF-8");

    public static TextFileEncoding Utf8Bom { get; } = new(new UTF8Encoding(true), true, "UTF-8 (BOM)");

    public static TextFileEncoding Gbk
    {
        get
        {
            EnsureCodePagesRegistered();
            return new TextFileEncoding(Encoding.GetEncoding(936), false, "GBK");
        }
    }

    public static void EnsureCodePagesRegistered()
    {
        if (_providerRegistered) return;
        lock (Gate)
        {
            if (_providerRegistered) return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
    }

    /// <summary>
    /// BOM 优先；没有 BOM 时严格验证 UTF-8。GBK 能接受更多字节序列，反过来猜会损坏真正的 UTF-8。
    /// </summary>
    public static TextFileEncoding Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Utf8Bom;

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new TextFileEncoding(new UnicodeEncoding(false, true), true, "UTF-16 LE");
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new TextFileEncoding(new UnicodeEncoding(true, true), true, "UTF-16 BE");
        }

        return IsValidUtf8(bytes) ? Utf8NoBom : Gbk;
    }

    internal static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private const char ByteOrderMarkChar = '\uFEFF';

    public string GetString(byte[] bytes)
    {
        var text = Encoding.GetString(bytes);
        return HasByteOrderMark && text.Length > 0 && text[0] == ByteOrderMarkChar ? text[1..] : text;
    }

    public byte[] GetBytes(string text)
    {
        var preamble = HasByteOrderMark ? Encoding.GetPreamble() : [];
        var body = Encoding.GetBytes(text);
        if (preamble.Length == 0) return body;

        var buffer = new byte[preamble.Length + body.Length];
        preamble.CopyTo(buffer, 0);
        body.CopyTo(buffer, preamble.Length);
        return buffer;
    }
}
