using Momoka.Diagnostics;
using Momoka.Emby;

namespace Momoka.Services;

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

    private readonly object _gate = new();
    private EmbySessionScope? _scope;
    private Version? _serverVersion;
    private long _generation;

    /// <inheritdoc />
    public Version? ServerVersion
    {
        get
        {
            EmbySessionScope? scope;
            Version? version;
            lock (_gate)
            {
                scope = _scope;
                version = _serverVersion;
            }
            return scope?.IsCurrent == true ? version : null;
        }
    }

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
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            _scope = null;
            _serverVersion = null;
        }

        try
        {
            var scope = session.Capture();
            var info = await scope.ExecuteAsync((client, ct) => client.GetSystemInfoAsync(ct), token)
                .ConfigureAwait(false);

            if (Version.TryParse(info.Version, out var version) && scope.IsCurrent && !token.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (generation != _generation) return;
                    _scope = scope;
                    _serverVersion = version;
                }
                Log.Debug(Category, $"服务器版本 {version}");
            }
        }
        catch (Exception error)
        {
            Log.Debug(Category, $"读取服务器版本失败：{error.Message}");
        }
    }
}
