using Koda.Core;
using Koda.Core.Playback;

namespace Koda.Core.Tests;

/// <summary>
/// The settings: what the listener chose, kept in one file, with defaults that hold
/// when the file is missing or damaged.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void ADamagedFileLeavesTheDefaults()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-broken-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ this is not json");

        var settings = Settings.Load(file);

        Assert.True(settings.CloseToTray);
        Assert.True(settings.Autoplay);
        Assert.Equal(100, settings.Volume);
        Assert.Equal(3, settings.LyricsSources.Count);
    }

    [Fact]
    public void AMissingFileLeavesTheDefaults()
    {
        var settings = Settings.Load(Path.Combine(Fixtures.Scratch, $"settings-gone-{Guid.NewGuid():N}.json"));

        Assert.True(settings.CloseToTray);
        Assert.Equal(100, settings.Volume);
    }

    [Fact]
    public void MusicVolumeMuteAndTheSelectedPanelSurviveRestart()
    {
        var path = Path.Combine(Fixtures.Scratch, $"player-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "volume": 42 }""");
        var settings = Settings.Load(path);
        Assert.False(settings.MusicMuted);
        Assert.Equal(NowPlayingPanel.None, settings.PlayerPanel);

        settings.Update(s =>
        {
            s.MusicMuted = true;
            s.PlayerPanel = NowPlayingPanel.Lyrics;
        });

        var restored = Settings.Load(path);
        Assert.Equal(42, restored.Volume);
        Assert.True(restored.MusicMuted);
        Assert.Equal(NowPlayingPanel.Lyrics, restored.PlayerPanel);
    }

    [Fact]
    public void WindowsMixerVolumePreservesSilenceSeparatelyFromThePlayerSlider()
    {
        var path = Path.Combine(Fixtures.Scratch, $"mixer-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "volume": 42 }""");
        var settings = Settings.Load(path);
        Assert.Null(settings.WindowsMixerVolume);

        settings.WindowsMixerVolume = 0;
        settings.Save();

        var restored = Settings.Load(path);
        Assert.Equal(42, restored.Volume);
        Assert.Equal(0f, restored.WindowsMixerVolume);
    }

    [Fact]
    public void LoudnessProfileSurvivesRestartAndOldSettingsKeepTheirToggle()
    {
        var path = Path.Combine(Fixtures.Scratch, $"loudness-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "normalizesVolume": false, "volume": 40 }""");
        var settings = Settings.Load(path);
        Assert.False(settings.NormalizesVolume);
        Assert.Equal(LoudnessLevel.Normal, settings.VolumeNormalizationLevel);

        settings.Update(s =>
        {
            s.NormalizesVolume = true;
            s.VolumeNormalizationLevel = LoudnessLevel.Quiet;
        });
        var restored = Settings.Load(path);
        Assert.True(restored.NormalizesVolume);
        Assert.Equal(LoudnessLevel.Quiet, restored.VolumeNormalizationLevel);
        Assert.Equal(40, restored.Volume);
    }

    [Fact]
    public void LosslessDownloadsHaveNoServerUntilOneIsTypedIn()
    {
        var settings = Settings.Load(Path.Combine(Fixtures.Scratch, $"settings-lossless-{Guid.NewGuid():N}.json"));
        settings.UseLosslessCredentialStore(new TestSecretStore());

        Assert.Null(settings.LosslessServer);

        // Turned on with nowhere to download from, it stays the ordinary download.
        settings.LosslessDownloads = true;
        Assert.False(settings.UsesLosslessServer);

        settings.ConfigureLosslessServer(new("https://example.com/a/token/", null));
        Assert.True(settings.UsesLosslessServer);
    }

    [Theory]
    [InlineData("https://example.com/a/token/", true)]
    [InlineData("http://192.168.1.4:8080/a/token", false)]
    [InlineData("http://127.0.0.1:8080/a/token", true)]
    [InlineData("http://[::1]:8080/a/token", true)]
    [InlineData("https://user:password@example.com/", false)]
    [InlineData("  https://example.com/  ", true)]
    [InlineData("example.com/a/token", false)]
    [InlineData("ftp://example.com/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ALosslessServerRequiresHttpsOffTheDevice(string? url, bool valid) =>
        Assert.Equal(valid, LosslessServer.IsValidUrl(url));

    [Fact]
    public void LegacyServerCredentialsAreMigratedAndReloadedFromTheStore()
    {
        const string oldSettings = """
            { "volume": 36, "losslessDownloads": true, "losslessServerUrl": "https://example.com/a/old-token/",
              "losslessServerSecret": "old-secret" }
            """;
        var file = Path.Combine(Fixtures.Scratch, $"settings-migrate-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, oldSettings);
        var settings = Settings.Load(file);
        var secrets = new TestSecretStore { FailWrites = true };

        Assert.Throws<IOException>(() => settings.UseLosslessCredentialStore(secrets));
        Assert.Equal(oldSettings, File.ReadAllText(file));

        secrets.FailWrites = false;
        settings.UseLosslessCredentialStore(secrets);
        var saved = File.ReadAllText(file);
        Assert.DoesNotContain("old-token", saved);
        Assert.DoesNotContain("old-secret", saved);

        var restarted = Settings.Load(file);
        restarted.UseLosslessCredentialStore(secrets);
        Assert.Equal(36, restarted.Volume);
        Assert.True(restarted.UsesLosslessServer);
        Assert.Equal(new LosslessServerConfiguration("https://example.com/a/old-token/", "old-secret"), restarted.LosslessServer);
    }

    [Fact]
    public void SavingReplacesTheSettingsFileAndLeavesNoTemporaryFile()
    {
        var name = $"settings-save-{Guid.NewGuid():N}.json";
        var file = Path.Combine(Fixtures.Scratch, name);
        File.WriteAllText(file, """{ "volume": 42 }""");
        var settings = Settings.Load(file);

        if (OperatingSystem.IsWindows())
        {
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                settings.Update(s => s.Volume = 76);
                Assert.Equal(42, Settings.Load(file).Volume);
            }
        }
        settings.Update(s => s.Volume = 82);

        Assert.Equal(82, Settings.Load(file).Volume);
        Assert.Empty(Directory.EnumerateFiles(Fixtures.Scratch, $"{name}.*.tmp"));
    }

    [Fact]
    public void TheChoicesSurviveASaveAndLoad()
    {
        var settings = new Settings
        {
            CloseToTray = false,
            Autoplay = false,
            Volume = 42,
            LyricsMotion = false,
            ShowsBackingVocals = false,
            DuetSides = false,
            SavesListeningHistory = false,
            SavesSearchHistory = false,
            Theme = Theme.Dark,
            LyricsFontSize = 26,
            NormalizesVolume = false,
            Crossfades = false,
            CrossfadeSeconds = 9,
            SharesOnDiscord = true,
            CacheLimit = CacheLimit.Large,
            DisabledLyricsSources = [LyricsSource.BetterLyrics],
        };

        var text = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<Settings>(text);

        Assert.NotNull(back);
        Assert.False(back!.CloseToTray);
        Assert.False(back.Autoplay);
        Assert.Equal(42, back.Volume);
        Assert.False(back.LyricsMotion);
        Assert.False(back.ShowsBackingVocals);
        Assert.False(back.DuetSides);
        Assert.False(back.SavesListeningHistory);
        Assert.False(back.SavesSearchHistory);
        Assert.Equal(Theme.Dark, back.Theme);
        Assert.Equal(26, back.LyricsFontSize);
        Assert.False(back.NormalizesVolume);
        Assert.False(back.Crossfades);
        Assert.Equal(9, back.CrossfadeSeconds);
        Assert.True(back.SharesOnDiscord);
        Assert.Equal(CacheLimit.Large, back.CacheLimit);
        Assert.False(back.IsEnabled(LyricsSource.BetterLyrics));
        Assert.True(back.IsEnabled(LyricsSource.YouLyPlus));
    }

    [Fact]
    public void TheLastServiceCannotBeTurnedOff()
    {
        var settings = new Settings();

        // The lyrics need somewhere to come from.
        settings.SetEnabled(LyricsSource.YouLyPlus, false);
        settings.SetEnabled(LyricsSource.BetterLyrics, false);
        settings.SetEnabled(LyricsSource.Lrclib, false);

        Assert.Single(settings.LyricsSources);
        Assert.True(settings.IsEnabled(LyricsSource.Lrclib));
    }

    [Fact]
    public void TheServicesAreAskedInTheOrderTheyWereSaved()
    {
        var settings = new Settings
        {
            LyricsSourceOrder = [LyricsSource.Lrclib, LyricsSource.YouLyPlus, LyricsSource.BetterLyrics],
            DisabledLyricsSources = [LyricsSource.YouLyPlus],
        };

        Assert.Equal([LyricsSource.Lrclib, LyricsSource.BetterLyrics], settings.LyricsSources);
    }

    [Fact]
    public void AServiceAddedLaterJoinsTheEndOfTheOrder()
    {
        // A file saved before a service existed still asks for it.
        var file = Path.Combine(Fixtures.Scratch, $"settings-order-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSourceOrder": [ "Lrclib" ] }""");

        var settings = Settings.Load(file);

        Assert.Equal(3, settings.LyricsSourceOrder.Count);
        Assert.Equal(LyricsSource.Lrclib, settings.LyricsSourceOrder[0]);
    }

    [Fact]
    public void TheOldLyricsSizeBecomesAPointSize()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-old-size-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "lyricsSize": "Large" }""");

        var settings = Settings.Load(file);

        Assert.Equal(24, settings.LyricsFontSize);
        // The old name reads as nothing, so it is never written back.
        Assert.Null(settings.LegacyLyricsSize);
    }

    [Fact]
    public void AFileWithoutTheNewerChoicesTakesTheirDefaults()
    {
        var file = Path.Combine(Fixtures.Scratch, $"settings-older-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, """{ "volume": 70 }""");

        var settings = Settings.Load(file);

        Assert.Equal(70, settings.Volume);
        Assert.True(settings.NormalizesVolume);
        Assert.True(settings.Crossfades);
        Assert.Equal(5, settings.CrossfadeSeconds);
        Assert.False(settings.SharesOnDiscord);
        Assert.Equal(20, settings.LyricsFontSize);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(40, 12)]
    [InlineData(6.4, 6)]
    [InlineData(double.NaN, 5)]
    public void TheOverlapStaysBetweenOneAndTwelveSeconds(double asked, double kept)
    {
        var settings = new Settings { CrossfadeSeconds = asked };

        Assert.Equal(kept, settings.CrossfadeSeconds);
    }
}
