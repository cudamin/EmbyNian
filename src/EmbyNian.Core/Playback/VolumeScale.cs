using EmbyNian.Configuration;

namespace EmbyNian.Playback;

/// <summary>The same linear volume range used by mpv and the integrated rail.</summary>
public static class VolumeScale
{
    public static double Clamp(double level) => double.IsFinite(level)
        ? Math.Clamp(level, 0, AudioSettings.MaxVolume)
        : 0;

    public static double Fraction(double level) => Clamp(level) / AudioSettings.MaxVolume;

    public static double Step(double level, double delta) => Clamp(Clamp(level) + delta);
}
