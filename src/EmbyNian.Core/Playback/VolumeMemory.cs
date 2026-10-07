using EmbyNian.Configuration;

namespace EmbyNian.Playback;

/// <summary>Separates a local volume gesture's echo window from the level awaiting persistence.</summary>
public sealed class VolumeMemory
{
    public const int SettleMilliseconds = 1200;

    private int? _observed;
    private int? _requested;
    private long _requestedAt;
    private long _request;

    public int? Pending { get; private set; }
    public long DueAt { get; private set; }
    public long Revision { get; private set; }

    /// <summary>During the echo-protection window the kernel's real reading is parked here, not discarded.</summary>
    public int? Queued { get; private set; }

    public static int? Level(double value) => double.IsFinite(value)
        ? (int)Math.Clamp(Math.Round(value), 0, AudioSettings.MaxVolume) : null;
    public long Request(int level, long now)
    {
        _requested = level;
        _requestedAt = now;
        return ++_request;
    }

    public bool Accept(long request, int level, long now)
    {
        if (request != _request || _requested != level) return false;
        Remember(level, now);
        return true;
    }

    public bool Reject(long request)
    {
        if (request != _request) return false;
        _requested = null;
        return true;
    }

    public int? Observe(PlayerStatus status, bool canControl, long now)
    {
        if (!canControl || !status.Loaded || !status.VolumeKnown || Level(status.Volume) is not { } level)
            return null;

        if (_requested is { } wanted && level != wanted && now - _requestedAt < SettleMilliseconds)
        {
            // The protection window exists to swallow the pre-request echo, not the kernel's later
            // reading. Park the real value; the save loop or the window's expiry adopts it.
            Queued = level;
            return null;
        }

        _requested = null;
        Queued = null;
        Remember(level, now);
        return level;
    }

    /// <summary>Adopts a parked real reading, for settle/stop paths that cannot wait for the window.</summary>
    public int? FlushQueued(long now)
    {
        if (Queued is not { } level) return null;
        Queued = null;
        _requested = null;
        Remember(level, now);
        return level;
    }

    public void Saved(long revision)
    {
        if (revision == Revision) Pending = null;
    }

    /// <summary>A new playback may have the same level; its first confirmed reading must still be accepted.</summary>
    public void ResetObservation()
    {
        _request++;
        _requested = null;
        _observed = null;
    }

    private void Remember(int level, long now)
    {
        if (_observed == level) return;
        _observed = level;
        Pending = level;
        DueAt = now + SettleMilliseconds;
        Revision++;
    }
}
