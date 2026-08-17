using System.Text;

namespace EmbyMpvClient.Mpv;

/// <summary>
/// How a config file was encoded on disk, so an edit can be written back the same way.
/// v1 read everything as UTF-8 and silently mangled GBK-saved configs; mpv itself reads
/// mpv.conf as UTF-8, but a user who edited it in an older Notepad may well have a GBK file.
/// </summary>
public sealed record TextFileEncoding(Encoding Encoding, bool HasByteOrderMark, string Description)
{
    private static readonly object Gate = new();
    private static bool _providerRegistered;

    public static TextFileEncoding Utf8NoBom { get; } = new(new UTF8Encoding(false), false, "UTF-8");

    public static TextFileEncoding Utf8Bom { get; } = new(new UTF8Encoding(true), true, "UTF-8 (BOM)");

    /// <summary>Code page 936. Registering the provider is required on .NET; it is idempotent.</summary>
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
    /// BOM wins; otherwise strict UTF-8 validation decides, because any byte sequence is
    /// valid GBK and guessing the other way round would corrupt real UTF-8 files.
    /// </summary>
    public static TextFileEncoding Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Utf8Bom;

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE) return new TextFileEncoding(new UnicodeEncoding(false, true), true, "UTF-16 LE");
            if (bytes[0] == 0xFE && bytes[1] == 0xFF) return new TextFileEncoding(new UnicodeEncoding(true, true), true, "UTF-16 BE");
        }

        return IsValidUtf8(bytes) ? Utf8NoBom : Gbk;
    }

    /// <summary>Pure ASCII counts as valid UTF-8, which is the right answer for either encoding.</summary>
    internal static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private const char ByteOrderMarkChar = '﻿';

    public string GetString(byte[] bytes)
    {
        var text = Encoding.GetString(bytes);
        return HasByteOrderMark && text.Length > 0 && text[0] == ByteOrderMarkChar ? text[1..] : text;
    }

    public byte[] GetBytes(string text)
    {
        byte[] preamble = HasByteOrderMark ? Encoding.GetPreamble() : [];
        var body = Encoding.GetBytes(text);
        if (preamble.Length == 0) return body;

        var buffer = new byte[preamble.Length + body.Length];
        preamble.CopyTo(buffer, 0);
        body.CopyTo(buffer, preamble.Length);
        return buffer;
    }
}
