using Koda.Core.Playback;

namespace Koda.Core.Tests;

public class LoudnessTests
{
    [Theory]
    [InlineData(LoudnessLevel.Quiet, -19, -13)]
    [InlineData(LoudnessLevel.Normal, -14, -8)]
    [InlineData(LoudnessLevel.Loud, -11, -5)]
    public void LoudTracksAreAttenuatedTowardEachTarget(LoudnessLevel level, int targetLufs, int changeDb)
    {
        Assert.Equal(targetLufs, LoudnessNormalization.TargetLufs(level));
        var gain = LoudnessNormalization.Gain(level, loudnessOffsetDb: 8);
        Assert.Equal(changeDb, 20 * Math.Log10(gain), precision: 8);
    }

    [Theory]
    [InlineData(LoudnessLevel.Quiet)]
    [InlineData(LoudnessLevel.Normal)]
    [InlineData(LoudnessLevel.Loud)]
    public void AQuietTrackIsNeverAmplified(LoudnessLevel level) =>
        Assert.Equal(1, LoudnessNormalization.Gain(level, loudnessOffsetDb: -12));

    [Fact]
    public void DisabledAndUnmeasuredAudioUseSafeGains()
    {
        Assert.Equal(1, LoudnessNormalization.Gain(null, loudnessOffsetDb: 8));
        Assert.Equal(1, LoudnessNormalization.Gain(LoudnessLevel.Normal, double.NaN));
        Assert.Equal(1, LoudnessNormalization.Gain(LoudnessLevel.Loud, double.PositiveInfinity));
        Assert.Equal(-5, 20 * Math.Log10(LoudnessNormalization.Gain(LoudnessLevel.Quiet, null)), precision: 8);
    }

    [Fact]
    public void CrossfadeLimitsTheCombinedGainWhileKeepingTheBalance()
    {
        Assert.Equal((0.4, 0.5), LoudnessNormalization.LimitOverlap(0.4, 0.5));
        var mix = LoudnessNormalization.LimitOverlap(0.8, 0.6);
        Assert.Equal(1, mix.Main + mix.Tail, precision: 8);
        Assert.Equal(0.8 / 0.6, mix.Main / mix.Tail, precision: 8);
    }
}
