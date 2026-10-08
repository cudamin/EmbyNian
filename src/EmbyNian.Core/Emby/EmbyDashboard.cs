namespace EmbyNian.Emby;

/// <summary>控制台一次读取的快照。分区失败与成功但为空必须能够区分。</summary>
public sealed record EmbyDashboardSnapshot(
    EmbySystemInfo System,
    DashboardSection<List<EmbyDashboardSession>> Sessions,
    DashboardSection<EmbyActivityResult> Activity);

public sealed record DashboardSection<T>(T? Value, string? Error) where T : class;

public sealed class EmbyDashboardSession
{
    public string? Id { get; set; }
    public string? UserName { get; set; }
    public string? DeviceName { get; set; }
    public string? Client { get; set; }
    public string? ApplicationVersion { get; set; }
    public DateTimeOffset? LastActivityDate { get; set; }
    public EmbyItem? NowPlayingItem { get; set; }
    public EmbyDashboardPlayState? PlayState { get; set; }
}

public sealed class EmbyDashboardPlayState
{
    public bool IsPaused { get; set; }
    public long? PositionTicks { get; set; }
    public string? PlayMethod { get; set; }
}

public sealed class EmbyActivityResult
{
    public List<EmbyActivityEntry> Items { get; set; } = [];
    public int TotalRecordCount { get; set; }
}

public sealed class EmbyActivityEntry
{
    public string? Name { get; set; }
    public string? Overview { get; set; }
    public string? ShortOverview { get; set; }
    public string? Severity { get; set; }
    public DateTimeOffset? Date { get; set; }
}
