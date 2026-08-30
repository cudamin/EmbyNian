using EmbyNian.Diagnostics;
using EmbyNian.Emby;

namespace EmbyNian.Services;

/// <summary>
/// What the server can do, asked once and remembered. Today that is only its version, which the sort menu
/// needs because one key exists from 4.7.3 onwards; anything else the client has to ask permission for
/// belongs here too rather than in a page.
/// </summary>
public interface IServerCapabilities
{
    /// <summary>
    /// The server's version, once <see cref="ProbeAsync"/> has answered, and null until then.
    /// </summary>
    Version? ServerVersion { get; }

    Task ProbeAsync(CancellationToken token = default);
}

/// <inheritdoc cref="IServerCapabilities"/>
public sealed class ServerCapabilities(EmbySession session) : IServerCapabilities
{
    private const string Category = "app";

    /// <inheritdoc />
    public Version? ServerVersion { get; private set; }

    /// <summary>
    /// Asks the server what version it is and remembers the answer for the rest of the session.
    /// <para>
    /// Fire and forget, and silent on failure. Nothing blocks on the answer: every caller treats a
    /// null version as "offer the key anyway", so a server that will not say is no worse off than one
    /// that has not been asked yet.
    /// </para>
    /// </summary>
    public async Task ProbeAsync(CancellationToken token = default)
    {
        try
        {
            var info = await session.ExecuteAsync((client, ct) => client.GetSystemInfoAsync(ct), token)
                .ConfigureAwait(false);

            if (Version.TryParse(info.Version, out var version))
            {
                ServerVersion = version;
                Log.Debug(Category, $"服务器版本 {version}");
            }
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取服务器版本失败：{error.Message}");
        }
    }
}
