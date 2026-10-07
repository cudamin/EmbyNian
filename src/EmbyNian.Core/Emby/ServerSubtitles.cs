namespace EmbyNian.Emby;

/// <summary>一次字幕管理对话固定的登录身份、媒体文件和轨道快照。</summary>
public sealed class ServerSubtitles
{
    private readonly EmbySessionScope _scope;
    private readonly string _itemId;
    private readonly string _sourceId;
    private readonly string? _sourcePath;
    private MediaSource _source;
    private TrackIdentity[] _snapshot;

    public ServerSubtitles(EmbySessionScope scope, EmbyItem item, MediaSource source)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || !item.MediaSources.Contains(source))
            throw new InvalidOperationException("字幕管理缺少有效的媒体文件");
        _scope = scope;
        _itemId = item.Id;
        _sourceId = source.Id;
        _sourcePath = source.Path;
        _source = source;
        _snapshot = Snapshot(source);
    }

    public bool IsCurrent => _scope.IsCurrent;

    public async Task<List<RemoteSubtitleInfo>> SearchAsync(string language)
    {
        _scope.ThrowIfNotCurrent();
        var result = await _scope.ExecuteAsync((client, token) =>
            client.SearchSubtitlesAsync(_itemId, _sourceId, language, token), CancellationToken.None).ConfigureAwait(false);
        _scope.ThrowIfNotCurrent();
        return result;
    }

    public async Task DownloadAsync(RemoteSubtitleInfo subtitle)
    {
        _scope.ThrowIfNotCurrent();
        if (string.IsNullOrWhiteSpace(subtitle.Id)) throw new InvalidOperationException("字幕候选缺少下载标识，请重新搜索");
        await _scope.ExecuteAsync((client, token) =>
            client.DownloadSubtitleAsync(_itemId, _sourceId, subtitle.Id, token), CancellationToken.None).ConfigureAwait(false);
        _scope.ThrowIfNotCurrent();
    }

    public async Task DeleteAsync(MediaStream stream)
    {
        _scope.ThrowIfNotCurrent();
        if (!_source.MediaStreams.Contains(stream) || !stream.IsSubtitle || !stream.IsExternal)
            throw new InvalidOperationException("字幕列表已变化，请刷新后重新确认");
        var expected = TrackIdentity.From(stream);
        if (!_snapshot.Contains(expected) || _snapshot.Count(track =>
                (track with { Index = -1 }) == (expected with { Index = -1 })) != 1)
            throw new InvalidOperationException("字幕信息已变化或无法区分同名文件，未执行删除；请在服务器管理界面核对");
        var current = await ReadSourceAsync().ConfigureAwait(false);
        var matches = current.SubtitleStreams.Where(track => track.Index == expected.Index).ToArray();
        if (!_snapshot.SequenceEqual(Snapshot(current)) || matches.Length != 1 || TrackIdentity.From(matches[0]) != expected)
            throw new InvalidOperationException("服务器字幕列表已变化，未执行删除；请刷新后重新确认");
        _scope.ThrowIfNotCurrent();
        await _scope.ExecuteAsync((client, token) =>
            client.DeleteSubtitleAsync(_itemId, expected.Index, token), CancellationToken.None).ConfigureAwait(false);
        _scope.ThrowIfNotCurrent();
    }

    public async Task<MediaSource> ReloadAsync()
    {
        var current = await ReadSourceAsync().ConfigureAwait(false);
        _source = current;
        _snapshot = Snapshot(current);
        return current;
    }

    private async Task<MediaSource> ReadSourceAsync()
    {
        _scope.ThrowIfNotCurrent();
        var item = await _scope.ExecuteAsync((client, token) =>
            client.GetItemAsync(_itemId, token, EmbyFields.Files), CancellationToken.None).ConfigureAwait(false);
        _scope.ThrowIfNotCurrent();
        var sources = item.MediaSources.Where(source => source.Id == _sourceId).ToArray();
        if (item.Id != _itemId || sources.Length != 1 || sources[0].Path != _sourcePath)
            throw new InvalidOperationException("媒体文件已变化，请关闭字幕管理后重新打开");
        return sources[0];
    }

    private static TrackIdentity[] Snapshot(MediaSource source) =>
        [.. source.SubtitleStreams.Select(TrackIdentity.From).OrderBy(track => track.Index)];

    private sealed record TrackIdentity(int Index, string Type, string? Codec, string? Language,
        string? Title, string? DisplayTitle, string? DisplayLanguage, bool External, bool Forced)
    {
        internal static TrackIdentity From(MediaStream stream) => new(stream.Index, stream.Type, stream.Codec,
            stream.Language, stream.Title, stream.DisplayTitle, stream.DisplayLanguage, stream.IsExternal, stream.IsForced);
    }
}
