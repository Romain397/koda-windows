using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using EnginePlaylist = Koda.Core.PlaylistPage;

namespace Koda.Pages;

/// <summary>A playlist: hero header and the numbered track table. Pages of tracks after the
/// first load by themselves as the list nears its end, with placeholder rows meanwhile.</summary>
public sealed partial class PlaylistPage : Page
{
    private string _id = "";
    private EnginePlaylist? _playlist;
    private ObservableCollection<TrackRow> _rows = [];
    private readonly List<Song> _songs = [];
    private string? _continuation;
    private string? _playlistSession;
    private bool _active;
    private bool _loadingPage;
    private Task<bool>? _loadMoreTask;
    private CancellationTokenSource? _loadCancellation;
    private HashSet<string> _seenContinuations = new(StringComparer.Ordinal);
    private bool _editing;
    private int _loadVersion;
    private int _playRequest;
    private ScrollViewer? _scroller;

    private PlaylistReference? Source => _playlist is { } page ? PlaylistReference.From(page.Playlist) : null;

    private bool CanEdit => _active && !_loadingPage && !_editing && _playlist is { CanEdit: true }
        && _playlistSession is { } session && App.Account.PlaylistSession == session;

    public PlaylistPage()
    {
        InitializeComponent();
        Tracks.ItemsSource = _rows;
        DownloadButton.Songs = _songs;
        SongMenu.Attach(Tracks, () => _songs, rowActions: TrackActions, playlist: () => Source,
            playNow: song => _ = PlayPlaylistAsync(song));
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        _active = true;
        App.Library.Changed += OnLibraryChanged;
        App.Account.Changed += OnAccountChanged;
        App.Account.PlaylistEdited += OnPlaylistEdited;
        PlaylistCovers.Changed += OnCoverChanged;
        if (args.Parameter is string id && id.Length > 0)
        {
            _id = id;
            _fallback = null;
            _ = LoadAsync();
        }
        else if (args.Parameter is PlaylistRequest request)
        {
            _id = request.Id;
            _fallback = request;
            _ = LoadAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        _loadVersion++;
        CancelLoad();
        App.Library.Changed -= OnLibraryChanged;
        App.Account.Changed -= OnAccountChanged;
        App.Account.PlaylistEdited -= OnPlaylistEdited;
        PlaylistCovers.Changed -= OnCoverChanged;
        base.OnNavigatedFrom(args);
    }

    /// <summary>A playlist to open with what to show if YouTube serves it without a page
    /// header, as it does for OLAK5uy_ audio playlists (docs\parsing.md).</summary>
    public sealed record PlaylistRequest(string Id, string Title, string? Subtitle, Artwork? Artwork, bool LoadAll = false);

    private PlaylistRequest? _fallback;

    private async Task<EnginePlaylist> FetchAsync(bool fresh, CancellationToken cancellation)
    {
        try
        {
            return await EngineHost.Music.PlaylistAsync(_id, fresh ? cancellation : default, fresh: fresh);
        }
        catch (MusicException error) when (error.Kind == MusicException.Reason.BadResponse && _id.StartsWith("OLAK5uy_"))
        {
            var songs = await EngineHost.Music.PlaylistSongsAsync("VL" + _id, limit: null, cancellation: cancellation);
            var title = _fallback?.Title ?? "Songs";
            return new EnginePlaylist(new PlaylistSummary(_id, title, null, _fallback?.Artwork ?? songs.FirstOrDefault()?.Artwork),
                _fallback?.Subtitle, $"{songs.Length} songs", null, [.. songs.Select(song => new PlaylistTrack(song))], null);
        }
    }

    private async Task LoadAsync(bool fresh = false)
    {
        var version = ++_loadVersion;
        CancelLoad();
        _loadCancellation = new CancellationTokenSource();
        var cancellation = _loadCancellation.Token;
        var session = App.Account.PlaylistSession;
        var loadAll = _fallback?.LoadAll == true;
        var previousCount = loadAll ? 0 : _rows.Count;
        var initial = _playlist is null;
        _loadingPage = true;
        _loadMoreTask = null;
        Error.Visibility = Visibility.Collapsed;
        MoreSkeleton.Content = null;
        MoreError.Visibility = Visibility.Collapsed;
        UpdateEdit();
        if (initial)
        {
            var skeleton = new StackPanel { Spacing = 28 };
            skeleton.Children.Add(Skeletons.Hero());
            skeleton.Children.Add(Skeletons.Rows(10, numbered: true));
            SkeletonHost.Content = skeleton;
        }
        try
        {
            var page = await FetchAsync(fresh, cancellation);
            if (version != _loadVersion) return;
            var tracks = page.Tracks.ToList();
            var continuation = page.Continuation;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (tracks.Count < previousCount && continuation is { } token)
            {
                if (!seen.Add(token)) throw MusicException.BadResponse(200);
                var more = await EngineHost.Music.PlaylistMoreAsync(token, cancellation);
                if (version != _loadVersion) return;
                tracks.AddRange(more.Tracks);
                continuation = more.Continuation;
            }

            _playlist = page;
            _playlistSession = session;
            _songs.Clear();
            _songs.AddRange(tracks.Select(track => track.Song));
            _rows = new ObservableCollection<TrackRow>(tracks.Select((track, index) =>
                new TrackRow(index + 1, track.Song, track.SetVideoId)));
            Tracks.ItemsSource = _rows;
            _continuation = continuation;
            _seenContinuations = seen;
            ShowHeader(page);
            Tracks.Visibility = Visibility.Visible;
            UpdateEmpty();
        }
        catch (Exception error)
        {
            if (version != _loadVersion) return;
            if (initial) Error.ShowError(error, () => _ = LoadAsync());
            else App.Shell?.Note($"Couldn't refresh the playlist. {Errors.Describe(error)}");
            return;
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loadingPage = false;
                SkeletonHost.Content = null;
                UpdateEdit();
            }
        }

        if (loadAll) await LoadRemainingAsync();
    }

    private void CancelLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void ShowHeader(EnginePlaylist page)
    {
        HeroTitle.Text = page.Playlist.Title;
        HeroAuthor.Text = page.Author ?? "";
        HeroAuthor.Visibility = page.Author is null ? Visibility.Collapsed : Visibility.Visible;
        HeroMeta.Text = page.DetailLine ?? "";
        UpdateCover();
        Description.Text = page.Description ?? "";
        Description.Visibility = page.Description is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.Title = page.Playlist.Title;
        DownloadButton.Refresh();
        // A long playlist arrives in pages; Download fetches the rest first.
        DownloadButton.AllSongs = AllSongsAsync;
        UpdateSave();
        UpdatePin();
    }

    private void UpdateCover()
    {
        if (Source is not { } source) return;
        HeroArt.Source = PlaylistCovers.ImageFor(source, 200);
        var menu = new MenuFlyout();
        PlaylistCovers.AddMenuItems(menu, source);
        CoverButton.Flyout = menu;
    }

    private void OnCoverChanged(PlaylistReference changed) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_active && Source?.SamePlaylist(changed) == true) UpdateCover();
    });

    private void Append(IEnumerable<PlaylistTrack> tracks)
    {
        foreach (var track in tracks)
        {
            _songs.Add(track.Song);
            _rows.Add(new TrackRow(_songs.Count, track.Song, track.SetVideoId));
        }
    }

    private void OnTracksLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is not null) return;
        _scroller = FindScroller(Tracks);
        if (_scroller is not null)
        {
            _scroller.ViewChanged += (_, _) =>
            {
                if (_scroller.VerticalOffset > _scroller.ScrollableHeight - 900)
                {
                    _ = LoadMoreAsync();
                }
            };
        }
    }

    private async Task<bool> LoadMoreAsync()
    {
        if (!_active || _loadingPage || _continuation is not { } token || _loadCancellation is not { } load)
            return false;

        var task = _loadMoreTask ??= LoadNextPageAsync(token, _loadVersion, load.Token);
        try { return await task; }
        finally { if (_loadMoreTask == task) _loadMoreTask = null; }
    }

    private async Task<bool> LoadNextPageAsync(string token, int version, CancellationToken cancellation)
    {
        MoreError.Visibility = Visibility.Collapsed;
        MoreSkeleton.Content = Skeletons.Rows(4, numbered: true);
        try
        {
            if (_seenContinuations.Contains(token)) throw MusicException.BadResponse(200);
            var more = await EngineHost.Music.PlaylistMoreAsync(token, cancellation);
            if (version != _loadVersion) return false;
            _seenContinuations.Add(token);
            Append(more.Tracks);
            _continuation = more.Continuation;
            DownloadButton.Refresh();
            UpdateEmpty();
            return true;
        }
        catch (Exception)
        {
            if (version == _loadVersion) MoreError.Visibility = Visibility.Visible;
            return false;
        }
        finally
        {
            if (version == _loadVersion)
            {
                MoreSkeleton.Content = null;
            }
        }
    }

    private void OnMoreRetryClick(object sender, RoutedEventArgs e)
    {
        if (_continuation is { } token && _seenContinuations.Contains(token)) _ = LoadAsync(fresh: true);
        else if (_fallback?.LoadAll == true) _ = LoadRemainingAsync();
        else _ = LoadMoreAsync();
    }

    private async Task<IReadOnlyList<Song>> AllSongsAsync() =>
        await LoadRemainingAsync() ? _songs.ToArray() : [];

    private async Task<bool> LoadRemainingAsync()
    {
        var version = _loadVersion;
        while (version == _loadVersion && _continuation is not null)
        {
            if (!await LoadMoreAsync()) break;
        }

        return version == _loadVersion && _continuation is null;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = PlayPlaylistAsync();

    private async Task PlayPlaylistAsync(Song? song = null, bool shuffle = false)
    {
        if (!_active || _loadingPage || Source is not { } source) return;
        var request = ++_playRequest;
        var version = _loadVersion;
        var profile = Profiles.ActiveKey;
        var waiting = _continuation is not null;
        PlayLoading.IsActive = waiting;
        PlayLoading.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        IReadOnlyList<Song> songs;
        try
        {
            songs = await AllSongsAsync();
        }
        finally
        {
            if (request == _playRequest)
            {
                PlayLoading.IsActive = false;
                PlayLoading.Visibility = Visibility.Collapsed;
            }
        }

        if (request != _playRequest || version != _loadVersion || Profiles.ActiveKey != profile) return;
        if (_continuation is not null)
        {
            App.Shell?.Note("Couldn't load every song in this playlist. Try playing it again.");
            return;
        }

        var playback = PlaybackService.Instance;
        if (shuffle) await playback.ShuffleAsync(songs, source);
        else if (song is not null) await playback.PlayNowAsync(song, songs, source);
        else await playback.PlayPlaylistAsync(songs, source);
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlayPlaylistAsync(shuffle: true);

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackRow row)
        {
            _ = PlayPlaylistAsync(row.Song);
        }
    }

    private IEnumerable<MenuFlyoutItemBase> TrackActions(TrackRow row)
    {
        if (!CanEdit || row.PlaylistSetVideoId is not { } setId || _playlistSession is not { } session
            || _playlist?.Playlist.Id is not { } id) yield break;

        yield return SongMenu.Item("Remove from this playlist", "\uE74D", () =>
            _ = RunEditAsync(session, () => App.Account.RemovePlaylistTrackAsync(session, id, row.Song.Id, setId)));

        var index = _rows.IndexOf(row);
        if (index > 0 && _rows[index - 1].PlaylistSetVideoId is { } previous)
        {
            yield return SongMenu.Item("Move up", "\uE74A", () =>
                _ = RunEditAsync(session, () => App.Account.MovePlaylistTrackAsync(session, id, setId, previous)));
        }
        if (index >= 0 && (index + 1 < _rows.Count
                ? _rows[index + 1].PlaylistSetVideoId is not null
                : _continuation is not null))
        {
            yield return SongMenu.Item("Move down", "\uE74B", () => _ = MoveDownAsync(row, session, id));
        }
    }

    private async Task MoveDownAsync(TrackRow row, string session, string id)
    {
        if (_rows.IndexOf(row) == _rows.Count - 1) await LoadMoreAsync();
        var index = _rows.IndexOf(row);
        if (index >= 0 && index + 1 < _rows.Count && row.PlaylistSetVideoId is { } setId
            && _rows[index + 1].PlaylistSetVideoId is { } next)
        {
            // Moving the next row before this one also works across a page boundary.
            await RunEditAsync(session, () => App.Account.MovePlaylistTrackAsync(session, id, next, setId));
        }
    }

    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (!CanEdit || _playlistSession is not { } session || _playlist?.Playlist is not { } playlist) return;
        var title = await Dialogs.AskYouTubePlaylistNameAsync(playlist.Title);
        if (title is not null && title != playlist.Title)
            await RunEditAsync(session, () => App.Account.RenamePlaylistAsync(session, playlist.Id, title));
    }

    private async Task RunEditAsync(string session, Func<Task> edit)
    {
        if (!CanEdit || _playlistSession != session) return;
        _editing = true;
        UpdateEdit();
        try
        {
            await edit();
            App.Shell?.Note("Playlist updated on YouTube Music.");
        }
        catch (Exception error)
        {
            App.Shell?.Note($"Couldn't update the YouTube Music playlist. {Errors.Describe(error)}");
        }
        finally
        {
            _editing = false;
            UpdateEdit();
        }
    }

    private void OnPlaylistEdited(string id) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_active && _playlist?.Playlist.Id == id && _playlistSession == App.Account.PlaylistSession)
            _ = LoadAsync(fresh: true);
    });

    private void OnAccountChanged() => DispatcherQueue.TryEnqueue(UpdateEdit);

    private void UpdateEdit()
    {
        RenameButton.Visibility = _playlist is { CanEdit: true } && _playlistSession is { } session
            && App.Account.PlaylistSession == session ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = CanEdit;
    }

    private void UpdateEmpty()
    {
        EmptyText.Visibility = _songs.Count == 0 && _continuation is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _playlist is { CanEdit: true }
            ? "This playlist is empty. Open any song's menu and choose Add to playlist, then YouTube Music."
            : "This playlist is empty.";
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateSave();
        UpdatePin();
    });

    private void UpdatePin()
    {
        if (Source is { } playlist) PinButton.Content = Navigator.PinLabel(playlist);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (Source is { } playlist) Navigator.TogglePin(playlist);
    }

    private void UpdateSave()
    {
        var saved = _playlist is { } page && App.Library.IsSavedPlaylist(page.Playlist.Id);
        SaveIcon.Glyph = saved ? "" : "";
        SaveIcon.Foreground = (Brush)Application.Current.Resources[saved ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(SaveButton, saved ? "Remove from Library" : "Save to Library");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } page)
        {
            App.Library.ToggleSave(page.Playlist);
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } deeper) return deeper;
        }

        return null;
    }
}
