using System;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

namespace Koda.Interop;

/// <summary>
/// System Media Transport Controls in MANUAL mode: no MediaPlayer auto-binding,
/// every update owned here, so the system surfaces stay ours alone. The
/// window-bound SMTC comes from Windows.Media.SystemMediaTransportControlsInterop.GetForWindow
/// (OS projection).
/// </summary>
public sealed class SmtcController : IDisposable
{
    private SystemMediaTransportControls? _smtc;
    private Action<SystemMediaTransportControlsButton>? _onButton;
    private DispatcherQueue? _dispatch;

    public void Bind(nint hwnd, DispatcherQueue dispatch, Action<SystemMediaTransportControlsButton> onButton)
    {
        _dispatch = dispatch;
        _onButton = onButton;
        var smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc = smtc;
        smtc.ButtonPressed += OnButtonPressed;
        smtc.IsEnabled = true;
        SetTransport(false, false, false);
        smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        // No track: the card is silent (Stopped) until the first content arrives.
        smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        if (_dispatch is { HasThreadAccess: true }) _onButton?.Invoke(button);
        else _dispatch?.TryEnqueue(() => _onButton?.Invoke(button));
    }

    /// <summary>Playback state changed - manual, from the PlaybackService.</summary>
    public void UpdatePlaybackState(bool playing, bool stopped = false)
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.PlaybackStatus = stopped ? MediaPlaybackStatus.Stopped
            : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
    }

    /// <summary>The card's track: title, artist and the artwork the flyout shows.</summary>
    public void UpdateTrack(string title, string artist, string? artworkUrl)
    {
        if (_smtc is null)
        {
            return;
        }

        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title;
        updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(artist) ? "YouTube Music" : artist;
        if (artworkUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var image))
        {
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(image);
        }
        else
        {
            updater.Thumbnail = null;
        }

        updater.Update();
    }

    /// <summary>Nothing playing: the card goes blank rather than stale.</summary>
    public void ClearTrack()
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
    }

    /// <summary>Transport enablement follows the queue: honest buttons on the flyout.</summary>
    public void SetTransport(bool hasTrack, bool canPrevious, bool canNext)
    {
        if (_smtc is null)
        {
            return;
        }

        _smtc.IsPlayEnabled = hasTrack;
        _smtc.IsPauseEnabled = hasTrack;
        _smtc.IsStopEnabled = hasTrack;
        _smtc.IsPreviousEnabled = canPrevious;
        _smtc.IsNextEnabled = canNext;
    }

    /// <summary>Timeline so the flyout slider and lock screen match the bar's scrubber.</summary>
    public void SetTimeline(double elapsedSeconds, double durationSeconds)
    {
        if (_smtc is null)
        {
            return;
        }

        if (!double.IsFinite(elapsedSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return;
        }

        var timeline = new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(durationSeconds),
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = TimeSpan.FromSeconds(durationSeconds),
            Position = TimeSpan.FromSeconds(Math.Clamp(elapsedSeconds, 0, durationSeconds)),
        };
        _smtc.UpdateTimelineProperties(timeline);
    }

    /// <summary>Playback handed off: card silent, manual button promises held true.</summary>
    public void Reset()
    {
        if (_smtc is null)
        {
            return;
        }

        ClearTrack();
        SetTransport(false, false, false);
    }

    public void Dispose()
    {
        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc = null;
        }
        _onButton = null;
        _dispatch = null;
    }
}
