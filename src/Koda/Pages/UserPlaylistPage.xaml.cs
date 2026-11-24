using System;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// A playlist made here: a mosaic of its first covers, the name and figures, Play and
/// Shuffle, and the songs in numbered rows. Each row's menu moves it up or down, takes it
/// out, or does anything a song can do; More renames or deletes the playlist.
/// </summary>
public sealed partial class UserPlaylistPage : Page
{
    private Guid _id;
    private UserPlaylist? _playlist;
    private Song[] _songs = [];

    public UserPlaylistPage()
    {
        InitializeComponent();
        SongMenu.Attach(Tracks, () => _songs, RowActions, playlist: () => Source);
        App.Library.Changed += OnLibraryChanged;
        PlaylistCovers.Changed += OnCoverChanged;
        Unloaded += (_, _) =>
        {
            App.Library.Changed -= OnLibraryChanged;
            PlaylistCovers.Changed -= OnCoverChanged;
        };
    }

    private PlaylistReference? Source => _playlist is { } playlist ? PlaylistReference.From(playlist) : null;

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is Guid id)
        {
            _id = id;
        }

        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void OnCoverChanged(PlaylistReference changed) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_playlist is not { } playlist || !PlaylistReference.From(playlist).SamePlaylist(changed)) return;
        BuildMosaic(playlist);
        BuildMore(playlist);
    });

    private void Render()
    {
        var playlist = _playlist = App.Library.UserPlaylist(_id);
        if (playlist is null)
        {
            // The page outlived its playlist (deleted on another page or in Settings).
            App.Shell?.NavigateRoot(typeof(HomePage));
            return;
        }

        _songs = playlist.Songs;
        DownloadButton.Title = playlist.Name;
        DownloadButton.Songs = playlist.Songs;
        HeroTitle.Text = playlist.Name;
        HeroDetail.Text = Format.SongList(_songs);
        Tracks.ItemsSource = playlist.Songs.Select((s, i) => new TrackRow(i + 1, s)).ToArray();
        BuildMosaic(playlist);

        var any = _songs.Length > 0;
        PlayButton.Visibility = ShuffleButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            Empty.Show("This playlist is empty", "Add songs from any song's menu: right-click a song and choose Add to playlist.", "\uE8FD",
                "Find songs", () => App.Shell?.SearchFor(string.Empty));
        }

        BuildMore(playlist);
    }

    private void BuildMosaic(UserPlaylist playlist)
    {
        // Keep the fallback glyph at the back; the covers go over it.
        while (Mosaic.Children.Count > 1)
        {
            Mosaic.Children.RemoveAt(1);
        }

        if (PlaylistCovers.ImageFor(PlaylistReference.From(playlist), 200, customOnly: true) is { } cover)
        {
            ShowSingleCover(new Image { Source = cover, Stretch = Stretch.UniformToFill });
            return;
        }

        // Distinct covers, as the Mac shows them: four tile the square, fewer is the first one whole.
        var seen = new HashSet<string>();
        var art = playlist.Songs.Select(s => s.Artwork).OfType<Artwork>()
            .Where(a => seen.Add(a.Url.ToString()))
            .Take(4)
            .ToArray();
        if (art.Length == 0)
        {
            MosaicTile.Background = AccentGradient();
            MosaicGlyph.Foreground = new SolidColorBrush(Colors.White);
            return;
        }

        MosaicTile.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        MosaicGlyph.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        if (art.Length < 4)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            Art.SetSource(image, art[0]);
            ShowSingleCover(image);
            return;
        }

        var corners = new[]
        {
            new CornerRadius(16, 0, 0, 0),
            new CornerRadius(0, 16, 0, 0),
            new CornerRadius(0, 0, 0, 16),
            new CornerRadius(0, 0, 16, 0),
        };
        for (var i = 0; i < 4; i++)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            Art.SetSource(image, art[i]);
            var tile = new Border
            {
                CornerRadius = corners[i],
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                Child = image,
            };
            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            Mosaic.Children.Add(tile);
        }
    }

    private void ShowSingleCover(Image image)
    {
        MosaicTile.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        MosaicGlyph.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var tile = new Border { CornerRadius = new CornerRadius(16), Child = image };
        Grid.SetColumnSpan(tile, 2);
        Grid.SetRowSpan(tile, 2);
        Mosaic.Children.Add(tile);
    }

    /// <summary>The Mac's empty playlist cover: the accent fading away behind a white note.</summary>
    private static Brush AccentGradient()
    {
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0xD9, accent.R, accent.G, accent.B), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x59, accent.R, accent.G, accent.B), Offset = 1 },
            },
        };
    }

    private void BuildMore(UserPlaylist playlist)
    {
        var menu = new MenuFlyout();
        var source = PlaylistReference.From(playlist);
        var hasSongs = playlist.Songs.Length > 0;
        var playNext = SongMenu.Item("Play next", "\uE8A5", () => _ = PlaybackService.Instance.PlayNextAsync(playlist.Songs[0], source));
        playNext.IsEnabled = hasSongs;
        menu.Items.Add(playNext);
        var queue = SongMenu.Item("Add to queue", "\uE71C", () => PlaybackService.Instance.AddToQueue(playlist.Songs, playlist: source));
        queue.IsEnabled = hasSongs;
        menu.Items.Add(queue);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Navigator.PinItem(source));
        PlaylistCovers.AddMenuItems(menu, source);
        menu.Items.Add(SongMenu.Item("Rename…", "\uE70F", () => _ = Dialogs.RenamePlaylistAsync(playlist)));
        var delete = SongMenu.Item($"Delete playlist…", "\uE74D", () => _ = DeleteAsync(playlist));
        menu.Items.Add(delete);
        MoreButton.Flyout = menu;
    }

    private async System.Threading.Tasks.Task DeleteAsync(UserPlaylist playlist)
    {
        if (await Dialogs.ConfirmAsync($"Delete “{playlist.Name}”?",
                "The songs stay wherever else you saved them.", "Delete Playlist"))
        {
            App.Library.DeletePlaylist(playlist.Id);
            App.Shell?.NavigateRoot(typeof(HomePage));
        }
    }

    private System.Collections.Generic.IEnumerable<MenuFlyoutItemBase> RowActions(Song song)
    {
        if (_playlist is not { } playlist)
        {
            yield break;
        }

        var index = Array.FindIndex(playlist.Songs, s => s.Id == song.Id);
        if (index < 0)
        {
            yield break;
        }

        var up = SongMenu.Item("Move up", "\uE74A", () => App.Library.MoveSongs([index], index - 1, playlist.Id));
        up.IsEnabled = index > 0;
        yield return up;

        var down = SongMenu.Item("Move down", "\uE74B", () => App.Library.MoveSongs([index], index + 2, playlist.Id));
        down.IsEnabled = index < playlist.Songs.Length - 1;
        yield return down;

        yield return SongMenu.Item("Remove from playlist", "\uE738", () => App.Library.RemoveSongs([index], playlist.Id));
    }

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, _songs, Source);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (Source is { } source) _ = PlaybackService.Instance.PlayPlaylistAsync(_songs, source);
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlaybackService.Instance.ShuffleAsync(_songs, Source);
}
