using System;
using System.Collections.ObjectModel;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;

namespace Koda.Pages;

/// <summary>
/// Now Playing, ported from the Mac's NowPlayingView: the cover's colours as a slowly moving
/// background, the cover large (easing back while paused), title and artist with the like
/// button, scrubber, transport and volume in one column; the queue panel (Shuffle, Repeat,
/// Autoplay, Up Next) beside it when the window is wide, in the cover's place when narrow.
/// Always dark, as on the Mac. Esc, Close or the player bar's cover close it.
/// </summary>
public sealed partial class NowPlayingPage : Page
{
    private bool QueueOpen => App.Settings.PlayerPanel == NowPlayingPanel.Queue;
    private bool LyricsOpen => App.Settings.PlayerPanel == NowPlayingPanel.Lyrics;

    /// <summary>Where a side panel is installed: the layout's column, or the narrow host.</summary>
    private enum PanelPlace
    {
        Layout,
        Narrow,
    }

    private PanelPlace _queuePlace = PanelPlace.Layout;
    private PanelPlace _lyricsPlace = PanelPlace.Layout;

    private PlaybackService Playback => PlaybackService.Instance;
    private ObservableCollection<QueueItem> _upcoming = [];
    private int _upcomingVersion = -1;
    private int _upcomingStart;
    private bool _active;
    private bool _windowVisible;
    private AppWindow? _appWindow;
    private bool _queueRefreshPending;
    private Song? _displayedSong;
    private PlaylistReference? _shownPlaylist;
    private string? _paletteFor;
    private bool _wide = true;
    private bool _scrubbing;
    private bool _dragging;
    private QueueItem? _dragged;
    private int _dragFrom;
    private int _dragVersion;
    private Guid? _dragCurrent;
    private PlayQueue.ClearedUpcoming? _cleared;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private ScrollViewer? _queueScroller;
    private readonly Windows.UI.ViewManagement.UISettings _systemUi = new();
    private bool SystemMotion => _systemUi.AnimationsEnabled && !EnergySaver.IsOn;
    private bool _backdropDrifting;
    private (bool Expanded, bool Animated)? _coverState;

    // MARK: lyrics

    /// <summary>The panel that shows the words and follows the song.</summary>
    private LyricsPanel? _lyrics;

    private string? _lyricsFor;

    public NowPlayingPage()
    {
        InitializeComponent();
        UpNext.ItemsSource = _upcoming;
        UpNext.ContextRequested += OnUpNextContext;

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(escape);

        Scrubbing.Attach(Scrubber, on => _scrubbing = on, Playback.SeekSeconds);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing) UpdateTimes(Scrubber.Value);
        };

        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _cleared = null;
            RenderQueueHeader();
        };

        // The lyrics: the panel follows the song, and the listener's own scrolling
        // takes over from it when they scroll away.
        _lyrics = new LyricsPanel(LyricsScroll, LyricsLines, DispatcherQueue);
        _lyrics.Seek = seconds => Playback.SeekSeconds(seconds);
        _lyrics.Share = (lines, index) =>
        {
            if (Playback.CurrentSong is { } song) _ = LyricsShare.ShowAsync(song, lines, index);
        };
        // The panel asks the player where the song is, every frame: the app's own
        // tick only comes twice a second, and a word that fills in half-second steps
        // reads as a sequence of keyframes rather than a word being sung.
        _lyrics.PositionSource = () => Playback.PositionSeconds;
        LyricsScroll.PointerPressed += (_, _) => _lyrics.UserScrolled = true;
        LyricsScroll.PointerWheelChanged += (_, _) => _lyrics.UserScrolled = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        _appWindow = App.Shell?.AppWindow;
        _windowVisible = IsPlayerWindowVisible();
        if (_appWindow is not null) _appWindow.Changed += OnPlayerWindowChanged;
        if (args.Parameter is "queue" && !QueueOpen)
        {
            // The player bar's queue button opens the queue directly. The two side panels
            // share one column, so asking for the queue also puts the lyrics away: they
            // were left open from an earlier visit and would otherwise draw on top of it.
            App.Settings.Update(settings => settings.PlayerPanel = NowPlayingPanel.Queue);
        }

        Playback.TrackChanged += OnTrackChanged;
        Playback.QueueChanged += OnQueueChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        Playback.VolumeChanged += UpdateVolume;
        Playback.SleepChanged += OnSleepChanged;
        App.Library.Changed += OnLibraryChanged;
        Profiles.Roster.Changed += OnProfileChanged;
        PlaylistCovers.Changed += OnPlaylistCoverChanged;
        App.Settings.Changed += OnSettingsChanged;
        EnergySaver.Changed += OnEnergySaverChanged;
        UpdateVolume();
        ApplySkipSettings();
        UpdateSleepPill();
        ApplyLayout();
        RenderTrack();
        RenderQueue();
        RenderPlayState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _active = false;
        if (_appWindow is not null) _appWindow.Changed -= OnPlayerWindowChanged;
        _appWindow = null;
        Playback.TrackChanged -= OnTrackChanged;
        Playback.QueueChanged -= OnQueueChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        Playback.PositionTick -= OnPositionTick;
        Playback.VolumeChanged -= UpdateVolume;
        Playback.SleepChanged -= OnSleepChanged;
        App.Library.Changed -= OnLibraryChanged;
        Profiles.Roster.Changed -= OnProfileChanged;
        PlaylistCovers.Changed -= OnPlaylistCoverChanged;
        App.Settings.Changed -= OnSettingsChanged;
        EnergySaver.Changed -= OnEnergySaverChanged;
        _lyrics?.Pause();
        _lyricsFor = null;
        _paletteFor = null;
        _displayedSong = null;
        _undoTimer.Stop();
        _cleared = null;
        _dragging = false;
        _dragged = null;
        _upcomingVersion = -1;
        Drift.Stop();
        _backdropDrifting = false;
    }

    private void OnTrackChanged() => DispatcherQueue.TryEnqueue(RenderTrack);
    private void OnQueueChanged()
    {
        if (_queueRefreshPending) return;
        _queueRefreshPending = DispatcherQueue.TryEnqueue(() =>
        {
            _queueRefreshPending = false;
            if (_active && _windowVisible && QueueOpen) RenderQueue();
        });
    }
    private void OnPlayStateChanged() => DispatcherQueue.TryEnqueue(RenderPlayState);
    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateLike();
        RenderPlaylistOrigin();
    });
    private void OnProfileChanged() => DispatcherQueue.TryEnqueue(() => RenderPlaylistOrigin());
    private void OnPlaylistCoverChanged(PlaylistReference changed) => DispatcherQueue.TryEnqueue(() =>
    {
        if (Playback.CurrentPlaylist?.SamePlaylist(changed) == true) RenderPlaylistOrigin(reloadCover: true);
    });
    private void OnSettingsChanged(Settings settings) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_active) return;
        ApplySkipSettings();
        RenderPlayState();
    });
    private void OnSleepChanged() => DispatcherQueue.TryEnqueue(UpdateSleepPill);
    private void OnEnergySaverChanged() => DispatcherQueue.TryEnqueue(RenderPlayState);

    private bool IsPlayerWindowVisible() => _appWindow is not { } window
        || (window.IsVisible && (window.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Minimized);

    private void OnPlayerWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var visible = IsPlayerWindowVisible();
        if (_windowVisible == visible) return;
        _windowVisible = visible;
        if (visible)
        {
            RenderTrack();
            if (QueueOpen) RenderQueue();
        }
        RenderPlayState();
    }

    // MARK: the song

    private void RenderTrack()
    {
        if (!_active || !_windowVisible) return;
        var song = Playback.CurrentSong;
        RenderPlaylistOrigin();
        Idle.Visibility = song is null ? Visibility.Visible : Visibility.Collapsed;
        Layout.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        if (song is null)
        {
            _displayedSong = null;
            return;
        }

        if (_displayedSong != song)
        {
            _displayedSong = song;
            TitleText.Text = song.Title;
            ArtistLink.Content = song.ArtistLine;
            ArtistLink.IsEnabled = song.Artists.Any(a => a.Id is not null);
            SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = true;
            Art.SetSource(Cover, song.Artwork);
            Art.SetSource(CompactCoverImage, song.Artwork);
            Art.SetSource(BackdropArt, song.Artwork);
            FailureBanner.Visibility = Visibility.Collapsed;
            UpdateLike();
            _ = PaintAsync(song);
        }
        OnPositionTick();
        if (LyricsOpen) _ = LoadLyricsAsync(song);
    }

    private void RenderPlaylistOrigin(bool reloadCover = false)
    {
        var source = Playback.CurrentPlaylist;
        if (!reloadCover && source == _shownPlaylist) return;
        _shownPlaylist = source;
        PlaylistOriginButton.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        PlaylistOriginName.Text = source?.Title ?? "";
        PlaylistOriginArt.Source = source is null ? null : PlaylistCovers.ImageFor(source, 26);
        ToolTipService.SetToolTip(PlaylistOriginButton, source is null ? null : $"Open playlist: {source.Title}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaylistOriginButton,
            source is null ? "" : $"Playing from {source.Title}. Open playlist");
    }

    private void OnPlaylistOriginClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentPlaylist is { } source) Navigator.OpenPlaylist(source);
    }

    // MARK: lyrics

    /// <summary>
    /// The lyrics for this song, from the services that answer best. They are asked for
    /// when the song starts (the panel is not the only thing that wants them), so the
    /// words are usually already there by the time the panel opens.
    /// </summary>
    private async System.Threading.Tasks.Task LoadLyricsAsync(Song song)
    {
        if (!_active || !_windowVisible || !LyricsOpen || _lyricsFor == song.Id)
        {
            return;
        }

        _lyricsFor = song.Id;
        LyricsEmpty.Visibility = Visibility.Collapsed;
        _lyrics?.Load(null);

        Lyrics? lyrics;
        try
        {
            lyrics = await LyricsService.For(song);
        }
        catch (Exception)
        {
            lyrics = null;
        }

        if (!_active || _lyricsFor != song.Id)
        {
            return; // another song took over while the services were answering
        }

        if (!_windowVisible || !LyricsOpen)
        {
            _lyricsFor = null;
            return;
        }

        _lyrics?.Load(lyrics);
        _lyrics?.Offset = App.Library.LyricsOffset(song.Id);
        if (lyrics is null)
        {
            LyricsEmpty.Visibility = Visibility.Visible;
            return;
        }

        if (SystemMotion) _lyrics?.PlayEntrance();
        UpdateLyricsPlayback();
        UpdateLyricsPosition();
    }
    /// <summary>The background takes the cover's three strongest colours.</summary>
    private async System.Threading.Tasks.Task PaintAsync(Song song)
    {
        if (_paletteFor == song.Id) return;
        _paletteFor = song.Id;
        // The last song's tint goes at once; the blurred cover carries the page meanwhile.
        Fade(BlobA, 0);
        Fade(BlobB, 0);
        if (await ArtAccent.PaletteAsync(song.Artwork) is not { Length: >= 2 } palette || !_active || _paletteFor != song.Id)
        {
            return;
        }

        BlobAColor.Color = palette[0];
        BlobAFade.Color = Windows.UI.Color.FromArgb(0, palette[0].R, palette[0].G, palette[0].B);
        BlobBColor.Color = palette[1];
        BlobBFade.Color = Windows.UI.Color.FromArgb(0, palette[1].R, palette[1].G, palette[1].B);
        Fade(BlobA, 0.55);
        Fade(BlobB, 0.45);
    }

    /// <summary>A plain storyboard fade. [scar] An OpacityTransition on these drifting
    /// rectangles fails the whole page's XAML load (XamlParseException at InitializeComponent).</summary>
    private void Fade(UIElement element, double to)
    {
        if (!SystemMotion)
        {
            element.Opacity = to;
            return;
        }

        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(800),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        story.Children.Add(animation);
        story.Begin();
    }

    private void UpdateLike()
    {
        var liked = Playback.CurrentSong is { } song && App.Library.IsLiked(song);
        LikeIcon.Glyph = liked ? "" : "";
        LikeIcon.Foreground = new SolidColorBrush(liked ? Windows.UI.Color.FromArgb(255, 0xFF, 0x4F, 0x8B) : Colors.White);
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(LikeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LikeButton, label);
    }

    private void RenderPlayState()
    {
        if (!_active) return;
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayIcon.Glyph = playing ? "" : "";
        PlayIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause (Space)" : "Play (Space)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");

        // The cover sits full size while sound plays, a little back while paused or loading.
        var expanded = playing && !loading;
        var motion = _windowVisible && SystemMotion;
        if (_coverState != (expanded, motion))
        {
            _coverState = (expanded, motion);
            if (motion) (expanded ? CoverIn : CoverOut).Begin();
            else
            {
                CoverIn.Stop();
                CoverOut.Stop();
                CoverScale.ScaleX = CoverScale.ScaleY = expanded ? 1 : 0.86;
            }
        }
        // The cover's colours drift behind the player, unless the listener turned that off,
        // the system has animations turned off, or Windows Energy Saver is on.
        var drift = playing && motion && App.Settings.AnimatesPlayerBackground;
        if (drift != _backdropDrifting)
        {
            _backdropDrifting = drift;
            if (drift) Drift.Begin();
            else Drift.Pause();
        }

        UpdateLyricsPlayback();
    }

    private void OnPositionTick()
    {
        var duration = Playback.DurationSeconds;
        if (!_windowVisible || _scrubbing || duration <= 0)
        {
            return;
        }

        Scrubber.Maximum = duration;
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, duration);
        UpdateTimes(Scrubber.Value);
        if (LyricsOpen) UpdateLyricsPosition();
    }

    private void UpdateLyricsPosition()
    {
        _lyrics?.SetPosition(Playback.PositionSeconds);
        _lyrics?.Follow();
    }

    private void UpdateLyricsPlayback()
    {
        _lyrics?.SetPlayback(_windowVisible && LyricsOpen && Playback.IsPlaying && !Playback.IsLoading,
            motion: SystemMotion && App.Settings.LyricsMotion);
    }

    private void UpdateTimes(double position)
    {
        var duration = Playback.DurationSeconds;
        ElapsedText.Text = Format(position);
        RemainingText.Text = "-" + Format(Math.Max(0, duration - position));
    }

    private static string Format(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        return total >= 3600 ? $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2}" : $"{total / 60}:{total % 60:D2}";
    }

    // MARK: the queue

    private void RenderQueue()
    {
        ShuffleToggle.IsChecked = Playback.Queue.IsShuffled;
        var repeat = Playback.Queue.Repeat;
        RepeatToggle.IsChecked = repeat != RepeatMode.Off;
        RepeatIcon.Glyph = repeat == RepeatMode.One ? "" : "";
        RepeatText.Text = repeat switch
        {
            RepeatMode.All => "Repeat all",
            RepeatMode.One => "Repeat one",
            _ => "Repeat",
        };
        AutoplayToggle.IsChecked = Playback.Autoplay;

        if (QueueOpen && !_dragging) SyncUpcoming();

        if (!Playback.IsExtending) MoreSkeleton.Content = null;
        else MoreSkeleton.Content ??= Skeletons.Rows(3);
        EndNote.Visibility = !Playback.Autoplay && Playback.Queue.Repeat == RepeatMode.Off ? Visibility.Visible : Visibility.Collapsed;
        EndNote.Text = _upcoming.Count == 0
            ? "Turn on Autoplay to keep going with songs like this one."
            : "The queue ends here. Autoplay would keep going with songs like the last one.";
        RenderQueueHeader();
    }

    private void SyncUpcoming()
    {
        var queue = Playback.Queue;
        var start = queue.UpcomingStart;
        var count = queue.UpcomingCount;
        if (queue.ItemsVersion == _upcomingVersion && start == _upcomingStart) return;

        // A normal advance removes the first row. Reuse the remaining rows and their scroll position.
        var skipped = start - _upcomingStart;
        if (queue.ItemsVersion == _upcomingVersion && skipped >= 0 && skipped <= 4 && _upcoming.Count - skipped == count)
        {
            for (var i = 0; i < skipped; i++) _upcoming.RemoveAt(0);
        }
        else if (_upcomingVersion >= 0 && count >= _upcoming.Count && count - _upcoming.Count <= 16
            && _upcoming.Select((item, index) => item.Id == queue.Items[start + index].Id).All(same => same))
        {
            for (var i = _upcoming.Count; i < count; i++) _upcoming.Add(queue.Items[start + i]);
        }
        else
        {
            // A new order is bound once, without one UI notification per track.
            _upcoming = new ObservableCollection<QueueItem>(queue.Items.Skip(start));
            UpNext.ItemsSource = _upcoming;
        }

        _upcomingVersion = queue.ItemsVersion;
        _upcomingStart = start;
    }

    private void RenderQueueHeader()
    {
        if (_cleared is { } cleared && !Playback.Queue.CanRestoreUpcoming(cleared))
        {
            _cleared = null;
            _undoTimer.Stop();
        }

        UpNextTitle.Text = _upcoming.Count > 0 ? "Up Next" : Playback.Queue.Repeat switch
        {
            RepeatMode.All when Playback.Queue.StartsShuffleRound => "New shuffle next",
            RepeatMode.All when Playback.Queue.Current is not null => "Queue repeats next",
            RepeatMode.One when Playback.Queue.Current is not null => "Current song repeats",
            _ => "Nothing up next",
        };
        UndoLink.Visibility = _cleared is not null ? Visibility.Visible : Visibility.Collapsed;
        ClearLink.Visibility = _cleared is null && _upcoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUpNextLoaded(object sender, RoutedEventArgs e)
    {
        var scroller = Descendant<ScrollViewer>(UpNext);
        if (_queueScroller == scroller) return;
        if (_queueScroller is not null) _queueScroller.ViewChanged -= OnQueueScrolled;
        _queueScroller = scroller;
        if (scroller is not null) scroller.ViewChanged += OnQueueScrolled;
    }

    private void OnQueueScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_active && QueueOpen && sender is ScrollViewer scroller
            && scroller.ScrollableHeight > 0 && scroller.VerticalOffset > scroller.ScrollableHeight - 400)
            _ = Playback.LoadMoreAsync();
    }

    private void OnUpNextClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item) _ = Playback.JumpAsync(item.Id);
    }

    private void OnUpNextContext(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: QueueItem item })
        {
            return;
        }

        var playlist = item.Source is { } source && source.ProfileKey == Profiles.ActiveKey ? source.Playlist : null;
        var menu = SongMenu.Build(item.Song, [], playlist: playlist,
            playNow: _song => _ = Playback.JumpAsync(item.Id));
        var remove = new MenuFlyoutItem { Text = "Remove from queue", Icon = new FontIcon { Glyph = "" } };
        remove.Click += (_, _) => Playback.Remove(item.Id);
        menu.Items.Insert(0, remove);
        menu.Items.Insert(1, new MenuFlyoutSeparator());
        if (args.TryGetPosition(UpNext, out var at)) menu.ShowAt(UpNext, at);
        else menu.ShowAt(UpNext);
        args.Handled = true;
    }

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (Playback.Queue.ItemsVersion != _upcomingVersion || Playback.Queue.UpcomingStart != _upcomingStart)
        {
            e.Cancel = true;
            return;
        }

        _dragging = true;
        _dragged = e.Items.FirstOrDefault() as QueueItem;
        _dragFrom = _dragged is null ? -1 : _upcoming.IndexOf(_dragged);
        _dragVersion = _upcomingVersion;
        _dragCurrent = Playback.Queue.Current?.Id;
    }

    private void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragging = false;
        if (Playback.Queue.ItemsVersion == _dragVersion && Playback.Queue.Current?.Id == _dragCurrent
            && _dragged is { } item && _dragFrom >= 0 && _upcoming.IndexOf(item) is var to and >= 0 && to != _dragFrom)
        {
            Playback.MoveUpcoming(_dragFrom, to);
        }

        _dragged = null;
        _upcomingVersion = -1;
        RenderQueue();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => Playback.SetShuffle(ShuffleToggle.IsChecked == true);

    private void OnRepeatClick(object sender, RoutedEventArgs e) => Playback.CycleRepeat();

    private void OnAutoplayClick(object sender, RoutedEventArgs e) => Playback.SetAutoplay(AutoplayToggle.IsChecked == true);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _cleared = Playback.ClearUpcoming();
        if (_cleared is not null) _undoTimer.Start();
        RenderQueueHeader();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_cleared is not { } cleared) return;
        _undoTimer.Stop();
        _cleared = null;
        Playback.RestoreUpcoming(cleared);
        RenderQueue();
    }

    // MARK: controls

    private void OnPlayClick(object sender, RoutedEventArgs e) => Playback.Toggle();
    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();
    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();
    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Playback.Skip(-App.Settings.SkipSeconds);
    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Playback.Skip(App.Settings.SkipSeconds);

    /// <summary>The skip ends follow the listener's interval in Settings, or go away when
    /// they turned the buttons off; the labels carry the seconds and the shortcut.</summary>
    private void ApplySkipSettings()
    {
        var settings = App.Settings;
        var seconds = settings.SkipSeconds;
        var shown = settings.ShowsSkipButtons;
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;

        SkipBackButton.Visibility = SkipForwardButton.Visibility = visibility;
        SkipBackColumn.Width = SkipForwardColumn.Width = shown ? GridLength.Auto : new GridLength(0);
        SkipBackIcon.Seconds = SkipForwardIcon.Seconds = seconds;

        var back = $"Back {seconds} seconds";
        var ahead = $"Ahead {seconds} seconds";
        ToolTipService.SetToolTip(SkipBackButton, $"{back} (Ctrl+Alt+Left)");
        ToolTipService.SetToolTip(SkipForwardButton, $"{ahead} (Ctrl+Alt+Right)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipBackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipForwardButton, ahead);
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        Playback.SetVolumePercent(e.NewValue);

    private void OnMuteClick(object sender, RoutedEventArgs e) => Playback.SetMuted(!Playback.IsMuted);

    private void UpdateVolume()
    {
        Volume.Value = Playback.VolumePercent;
        VolumeMuteIcon.Glyph = Playback.IsMuted ? "\uE74F" : "\uE767";
        var label = Playback.IsMuted ? "Unmute" : "Mute";
        ToolTipService.SetToolTip(VolumeMuteButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VolumeMuteButton, label);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song) App.Library.ToggleLike(song);
    }

    private void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong?.Artists.FirstOrDefault(a => a.Id is not null) is { Id: { } id })
        {
            Navigator.Go(typeof(ArtistPage), id);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Playback.Queue.Current is { } item)
        {
            var menu = SongMenu.Build(item.Song, [], playlist: Playback.CurrentPlaylist,
                playNow: _song => _ = Playback.JumpAsync(item.Id));
            menu.Items.Insert(0, BuildSleepMenu());
            menu.Items.Insert(1, new MenuFlyoutSeparator());
            menu.ShowAt(MoreButton);
        }
    }

    /// <summary>The top bar's pill opens the same timer choices, without going through More.</summary>
    private void OnSleepPillClick(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        AddSleepItems(menu.Items);
        menu.ShowAt(SleepPill);
    }

    private MenuFlyoutSubItem BuildSleepMenu()
    {
        var submenu = new MenuFlyoutSubItem
        {
            Text = "Sleep timer",
            Icon = new FontIcon { Glyph = "\uE708" },
        };
        AddSleepItems(submenu.Items);
        return submenu;
    }

    /// <summary>The presets, Off and End of song. A fresh menu is built each time, so the
    /// check marks always say what is really set.</summary>
    private void AddSleepItems(IList<MenuFlyoutItemBase> items)
    {
        var off = new ToggleMenuFlyoutItem
        {
            Text = "Off",
            IsChecked = !Playback.IsSleepArmed,
        };
        off.Click += (_, _) => Playback.CancelSleep();
        items.Add(off);
        items.Add(new MenuFlyoutSeparator());

        foreach (var minutes in PlaybackService.SleepPresets)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{minutes} minutes",
                IsChecked = Playback.SleepMinutes == minutes,
            };
            var chosen = minutes;
            item.Click += (_, _) => Playback.SleepIn(chosen);
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());
        var end = new ToggleMenuFlyoutItem
        {
            Text = "End of song",
            IsChecked = Playback.SleepAtEnd,
        };
        end.Click += (_, _) =>
        {
            if (Playback.SleepAtEnd)
            {
                Playback.CancelSleep();
            }
            else
            {
                Playback.SleepAtEndOfSong();
            }
        };
        items.Add(end);
    }

    /// <summary>The pill carries the countdown once a timer is set.</summary>
    private void UpdateSleepPill()
    {
        var label = Playback.SleepLabel;
        SleepPill.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        if (label is not null)
        {
            SleepPillText.Text = label;
        }

        ToolTipService.SetToolTip(SleepPill, label is null ? "Sleep timer" : $"Sleep timer: {label}");
    }

    private void OnOutputClick(object sender, RoutedEventArgs e) => AudioOutputMenu.Show(OutputButton);

    /// <summary>The overlay is the player in miniature: leaving the full page first keeps
    /// the library from sitting behind it in a second player view.</summary>
    private void OnMiniClick(object sender, RoutedEventArgs e)
    {
        App.Shell?.ShowMiniPlayer();
        Close();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnQueueToggleClick(object sender, RoutedEventArgs e) =>
        SetPanel(QueueToggle.IsChecked == true ? NowPlayingPanel.Queue : NowPlayingPanel.None);

    internal void ShowQueue(bool toggle = false) =>
        SetPanel(toggle && QueueOpen ? NowPlayingPanel.None : NowPlayingPanel.Queue);

    private void OnLyricsToggleClick(object sender, RoutedEventArgs e) =>
        SetPanel(LyricsToggle.IsChecked == true ? NowPlayingPanel.Lyrics : NowPlayingPanel.None);

    private void SetPanel(NowPlayingPanel panel)
    {
        if (App.Settings.PlayerPanel != panel) App.Settings.Update(settings => settings.PlayerPanel = panel);
        ApplyLayout();
        if (QueueOpen) RenderQueue();
        if (LyricsOpen && Playback.CurrentSong is { } song)
        {
            _ = LoadLyricsAsync(song);
            UpdateLyricsPosition();
        }
        UpdateLyricsPlayback();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Navigator.Go(typeof(HomePage));
    }

    // MARK: layout

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The blooms are larger than the page so their edges never show as they drift.
        var mx = e.NewSize.Width * 0.35;
        var my = e.NewSize.Height * 0.35;
        BlobA.Margin = BlobB.Margin = new Thickness(-mx, -my, -mx, -my);
        // The blurred cover a little larger than the page, so its soft edges stay out of view.
        BackdropArt.Margin = new Thickness(-e.NewSize.Width * 0.12, -e.NewSize.Height * 0.12, -e.NewSize.Width * 0.12, -e.NewSize.Height * 0.12);
        var wide = e.NewSize.Width >= 900 && e.NewSize.Height >= 560;
        if (wide != _wide)
        {
            _wide = wide;
            ApplyLayout();
        }
    }

    /// <summary>Wide: the player left, the queue panel beside it. Narrow: one column, the queue
    /// taking the cover's place with a compact header above it.</summary>
    private void ApplyLayout()
    {
        var queue = QueueOpen;
        var lyrics = LyricsOpen;
        QueueToggle.IsChecked = queue;
        LyricsToggle.IsChecked = lyrics;
        var open = queue || lyrics;
        QueuePanel.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsView.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;

        PlaceQueuePanel();
        PlaceLyricsPanel();

        if (_wide)
        {
            PanelColumn.Width = open ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            Layout.ColumnSpacing = open ? 64 : 0;
            CoverBox.Visibility = Visibility.Visible;
            CoverRow.Height = new GridLength(1, GridUnitType.Star);
            NarrowQueueRow.Height = new GridLength(0);
            ShowCompactCover(false);
        }
        else
        {
            PanelColumn.Width = new GridLength(0);
            Layout.ColumnSpacing = 0;
            CoverBox.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            CoverRow.Height = open ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            NarrowQueueRow.Height = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ShowCompactCover(open);
        }

        PlayerPanel.MaxWidth = _wide ? 460 : 560;
    }

    /// <summary>
    /// Where the queue panel lives: in the layout's panel column when the window is wide,
    /// in the cover's place when it is narrow.
    ///
    /// The place is kept as state, not read from the element: an element that is a
    /// content presenter's content has no Parent, so asking the element where it is
    /// answers "nowhere" - which is how the panel used to be left behind in the narrow
    /// host after the window grew again. An element can also only be installed in one
    /// place at a time, so it is released before it is moved.
    /// </summary>
    private void PlaceQueuePanel()
    {
        var want = _wide ? PanelPlace.Layout : PanelPlace.Narrow;
        if (_queuePlace == want)
        {
            return;
        }

        if (_queuePlace == PanelPlace.Narrow)
        {
            NarrowQueueHost.Content = null;
        }
        else
        {
            Layout.Children.Remove(QueuePanel);
        }

        if (want == PanelPlace.Narrow)
        {
            NarrowQueueHost.Content = QueuePanel;
        }
        else
        {
            Layout.Children.Add(QueuePanel);
            Grid.SetRow(QueuePanel, 1);
            Grid.SetColumn(QueuePanel, 1);
        }

        _queuePlace = want;
    }

    /// <summary>The same two places for the lyrics panel, with a host of its own so the two
    /// panels never have to trade places.</summary>
    private void PlaceLyricsPanel()
    {
        var want = _wide ? PanelPlace.Layout : PanelPlace.Narrow;
        if (_lyricsPlace == want)
        {
            return;
        }

        if (_lyricsPlace == PanelPlace.Narrow)
        {
            NarrowLyricsHost.Content = null;
        }
        else
        {
            Layout.Children.Remove(LyricsView);
        }

        if (want == PanelPlace.Narrow)
        {
            NarrowLyricsHost.Content = LyricsView;
        }
        else
        {
            Layout.Children.Add(LyricsView);
            Grid.SetRow(LyricsView, 1);
            Grid.SetColumn(LyricsView, 1);
        }

        _lyricsPlace = want;
    }

    private void ShowCompactCover(bool show)
    {
        CompactCover.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactCoverColumn.Width = show ? new GridLength(64) : new GridLength(0);
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}



