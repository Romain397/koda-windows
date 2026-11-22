using System.Text.Json;
using Koda.Core.Parsing;
using Koda.Playback;

namespace Koda.Core.Tests;

public class PlaylistPinsTests
{
    [Fact]
    public void MovingPinsUsesTheirIdentityAndPersistsTheChosenOrder()
    {
        var folder = Path.Combine(Fixtures.Scratch, $"pin-order-{Guid.NewGuid():N}");
        var library = new Library(folder);
        var first = new PlaylistReference("PLfirst", "First", false);
        var second = PlaylistReference.From(library.CreatePlaylist("Second"));
        var last = new PlaylistReference("PLlast", "Last", false);
        library.SetPlaylistPinned(last, true);
        library.SetPlaylistPinned(second, true);
        library.SetPlaylistPinned(first, true);

        library.MovePinnedPlaylist(first with { Title = "Renamed" }, last, after: true);
        Assert.Equal([second, last, first], library.PinnedPlaylists);
        library.MovePinnedPlaylist(first, second, after: false);
        library.MovePinnedPlaylist(last, first, after: true);
        library.MovePinnedPlaylist(first, first, after: true);
        library.MovePinnedPlaylist(second, new PlaylistReference("PLmissing", "Missing", false), after: false);

        var reopened = new Library(folder);
        Assert.Equal([first, last, second], reopened.PinnedPlaylists);
        reopened.Open(Path.Combine(folder, "other-profile"));
        reopened.MovePinnedPlaylist(first, second, after: true);
        Assert.Empty(reopened.PinnedPlaylists);
    }

    [Fact]
    public void PinsPersistAndFollowOnlyTheirLibrary()
    {
        var folder = Path.Combine(Fixtures.Scratch, Guid.NewGuid().ToString());
        var otherFolder = Path.Combine(Fixtures.Scratch, Guid.NewGuid().ToString());
        var library = new Library(folder);
        var local = library.CreatePlaylist("Local");
        var localRef = PlaylistReference.From(local);
        var remote = PlaylistReference.From(new PlaylistSummary("VLPLmine", "Remote", null, null));
        library.SetPlaylistPinned(localRef, true);
        library.SetPlaylistPinned(remote, true);
        library.SetPlaylistPinned(remote with { Title = "An old title" }, true);
        library.RenamePlaylist(local.Id, "Local renamed");
        library.UpdateSavedPlaylistTitle("PLmine", "Remote renamed");

        library = new Library(folder);
        Assert.Collection(library.PinnedPlaylists,
            pin => Assert.Equal("Remote renamed", library.ResolvePlaylist(pin)?.Title),
            pin => Assert.Equal("Local renamed", library.ResolvePlaylist(pin)?.Title));

        library.Open(otherFolder);
        Assert.Empty(library.PinnedPlaylists);
        library.Open(folder);
        Assert.True(library.IsPinned(remote));
        library.SetPlaylistPinned(remote, false);
        library.DeletePlaylist(local.Id);
        Assert.Empty(new Library(folder).PinnedPlaylists);
    }

    [Fact]
    public void PlaylistOriginStaysWithEachOccurrenceThroughEditsAndRestart()
    {
        var song = Pages.PlaylistSongs(Fixtures.Load("playlist.json"))[0];
        var source = new PlaylistSource("root", new PlaylistReference("PLmine", "Remote", false));
        var other = new PlaylistSource("root", new PlaylistReference("PLother", "Other playlist", false));
        var queue = new PlayQueue();
        queue.Replace([song, song], source: source);
        queue.PlayNext([song], other);
        queue.Append([song]);
        queue.SetShuffle(true);
        queue.SetShuffle(false);

        var saved = JsonSerializer.Deserialize<PlayQueue.Saved>(JsonSerializer.Serialize(queue.Save()))!;
        queue = PlayQueue.Restore(saved);
        Assert.Equal(source, queue.Current?.Source);
        Assert.Equal(other, queue.Advance(automatic: false)?.Source);
        Assert.Equal(source, queue.Advance(automatic: false)?.Source);
        Assert.Null(queue.Advance(automatic: false)?.Source);

        var legacy = new PlayQueue.Saved([song], 0, false, RepeatMode.Off);
        Assert.Null(PlayQueue.Restore(legacy).Current?.Source);
    }
}
