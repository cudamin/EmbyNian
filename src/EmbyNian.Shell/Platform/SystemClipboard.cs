using Windows.ApplicationModel.DataTransfer;

namespace EmbyNian.Shell.Platform;

/// <summary>
/// The clipboard, as the one thing this app puts on it.
/// <para>
/// Narrow on purpose. 复制日志 is the only caller, and what it needs is 「take this text」 — not a data
/// package, not a format negotiation. Reading the clipboard is deliberately absent: nothing in this app
/// pastes, and an unused <c>GetText</c> would be an async round trip and a permission story to maintain
/// for no caller.
/// </para>
/// </summary>
internal interface IClipboard
{
    /// <summary>
    /// Replaces the clipboard's contents with <paramref name="text"/>. Throws rather than reporting
    /// failure: every caller is a command that already has to say something to the user when this does not
    /// work, so it is already inside a <c>try</c>.
    /// </summary>
    void SetText(string text);
}

/// <inheritdoc cref="IClipboard"/>
/// <remarks>
/// Named <c>SystemClipboard</c> rather than <c>Clipboard</c> so that this file can name
/// <see cref="Clipboard"/> — the WinRT one — without the two shadowing each other.
/// </remarks>
internal sealed class SystemClipboard : IClipboard
{
    /// <inheritdoc />
    public void SetText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}
