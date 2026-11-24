namespace Koda.Core.Playback;

public enum LoudnessLevel
{
    Quiet,
    Normal,
    Loud,
}

public static class LoudnessNormalization
{
    private const int ReferenceLufs = -14;

    public static int TargetLufs(LoudnessLevel level) => level switch
    {
        LoudnessLevel.Quiet => -19,
        LoudnessLevel.Loud => -11,
        _ => ReferenceLufs,
    };

    /// <summary>Attenuate toward the target using YouTube's offset from the reference level.
    /// With no usable offset, apply only the attenuation of the Quiet profile.</summary>
    public static double Gain(LoudnessLevel? level, double? loudnessOffsetDb)
    {
        if (level is not { } preset) return 1;
        var offset = loudnessOffsetDb is { } value && double.IsFinite(value) ? value : 0;
        // The stream carries no true-peak measurement, so a positive gain has no safe headroom.
        var correction = Math.Min(0, TargetLufs(preset) - ReferenceLufs - offset);
        return Math.Pow(10, correction / 20);
    }

    /// <summary>Keep the combined peak of two overlapping tracks within the selected volume.</summary>
    public static (double Main, double Tail) LimitOverlap(double main, double tail)
    {
        var sum = main + tail;
        return sum > 1 ? (main / sum, tail / sum) : (main, tail);
    }
}
