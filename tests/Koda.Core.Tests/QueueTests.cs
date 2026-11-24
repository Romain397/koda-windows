using System.Text.Json;
using Koda.Core;
using Koda.Core.Parsing;
using Koda.Playback;

namespace Koda.Core.Tests;

public class QueueTests
{
    [Fact]
    public void RadioQueueStartsWithTheSeed()
    {
        var queue = Pages.Queue(Fixtures.Load("next-song.json"));

        Assert.Equal("J7p4bzqLvCw", queue.Songs[0].Id);
        Assert.Equal("Blinding Lights", queue.Songs[0].Title);
        Assert.Equal("I6Z2fqEVbPQ", queue.Songs[1].Id);
        Assert.Equal("São Paulo", queue.Songs[1].Title);
        Assert.Equal(50, queue.Songs.Length);
        Assert.NotNull(queue.Continuation);
        Assert.All(queue.Songs, s => Assert.False(s.IsVideo));
        Assert.All(queue.Songs, s => Assert.NotEqual("", s.ArtistLine));
    }

    [Fact]
    public void EmptyRootParsesAsEmptyQueue()
    {
        var queue = Pages.Queue(Json.Parse("{}"));
        Assert.Empty(queue.Songs);
        Assert.Null(queue.Continuation);
    }

    [Fact]
    public void ShuffledPlaylistMakesANewCompleteRoundWhenRepeatIsEnabledAtTheEnd()
    {
        var songs = PlaylistSongs(120);
        var queue = new PlayQueue();
        queue.Replace(songs, shuffled: true, source: Playlist);
        var order = queue.Items.Select(item => item.Id).ToArray();
        Assert.Equal(songs.Select(song => song.Id).Order(), queue.Items.Select(item => item.Song.Id).Order());
        Assert.Equal(0, queue.CurrentIndex);

        foreach (var id in order.Skip(1))
        {
            Assert.Equal(id, queue.Advance(automatic: true)?.Id);
            queue.TrimHistory(keeping: 50);
        }

        Assert.Null(queue.Advance(automatic: true));
        queue.Repeat = RepeatMode.All;
        for (var round = 0; round < 2; round++)
        {
            var last = queue.Current!.Song.Id;
            var next = queue.PeekNext(automatic: true);
            Assert.NotNull(next);
            Assert.NotEqual(last, next.Song.Id);
            Assert.Equal(next, queue.PeekNext(automatic: false));
            Assert.Equal(next, queue.Advance(automatic: true));

            var newOrder = queue.Items.Select(item => item.Id).ToArray();
            Assert.False(order.SequenceEqual(newOrder));
            Assert.Equal(order.Order(), newOrder.Order());

            foreach (var id in newOrder.Skip(1))
            {
                Assert.Equal(id, queue.PeekNext(automatic: true)?.Id);
                Assert.Equal(id, queue.Advance(automatic: true)?.Id);
                queue.TrimHistory(keeping: 50);
            }

            order = newOrder;
        }
    }

    [Fact]
    public void PreviousAtTheStartOfANewShuffleReturnsToTheTrackActuallyPlayed()
    {
        var queue = new PlayQueue { Repeat = RepeatMode.All };
        queue.Replace(PlaylistSongs(12), shuffled: true, source: Playlist);
        var previous = queue.Items[^1];
        Assert.True(queue.Jump(previous.Id));
        var next = queue.PeekNext(automatic: false);

        Assert.Equal(next, queue.Advance(automatic: false));
        Assert.Equal(previous, queue.Retreat());
        Assert.Equal(next, queue.PeekNext(automatic: false));
        Assert.Equal(next, queue.Advance(automatic: false));
    }

    [Fact]
    public void SequentialRepeatWrapsAndRepeatOneKeepsTheCurrentSong()
    {
        var queue = new PlayQueue { Repeat = RepeatMode.All };
        var songs = PlaylistSongs(3);
        queue.Replace(songs, start: 2, source: Playlist);
        Assert.Equal(songs[0], queue.Advance(automatic: true)?.Song);
        Assert.Equal(songs, queue.Items.Select(item => item.Song));

        queue.SetShuffle(true);
        queue.Repeat = RepeatMode.One;
        Assert.True(queue.Jump(queue.Items[^1].Id));
        Assert.Equal(queue.Current, queue.Advance(automatic: true));
    }

    [Fact]
    public void APartialSavedPlaylistAppendsMissingTracksBeforeTheNextShuffle()
    {
        var songs = PlaylistSongs(120);
        var saved = new PlayQueue.Saved(songs.Take(30).ToArray(), 29, true, RepeatMode.All,
            Sources: Enumerable.Repeat<PlaylistSource?>(Playlist, 30).ToArray());
        var queue = PlayQueue.Restore(saved);
        var current = queue.Current;
        var oldPreview = queue.PeekNext(automatic: true);

        Assert.Equal(Playlist, queue.IncompletePlaylist);
        Assert.True(queue.CompleteRestoredPlaylist(Playlist, songs));
        Assert.Null(queue.IncompletePlaylist);
        Assert.Equal(current, queue.Current);
        Assert.Equal(90, queue.UpcomingCount);
        Assert.NotEqual(oldPreview, queue.PeekNext(automatic: true));
        Assert.Equal(songs.Skip(30).Select(song => song.Id).Order(),
            queue.Upcoming.Select(item => item.Song.Id).Order());
        Assert.Null(PlayQueue.Restore(queue.Save()).IncompletePlaylist);
    }

    [Fact]
    public void TwoSongShuffleDoesNotRepeatTheLastSongImmediately()
    {
        var queue = new PlayQueue { Repeat = RepeatMode.All };
        queue.Replace(PlaylistSongs(2), shuffled: true, source: Playlist);
        queue.Advance(automatic: true);
        var last = queue.Current;
        Assert.NotEqual(last, queue.Advance(automatic: true));
    }

    [Fact]
    public void SavedShuffleKeepsPlaybackOrderAndRestoresPlaylistOrderWhenDisabled()
    {
        var songs = PlaylistSongs(65);
        var queue = new PlayQueue { Repeat = RepeatMode.All };
        queue.Replace(songs, start: 60, shuffled: true, source: Playlist);
        Assert.Equal(songs[60], queue.Current?.Song);
        var order = queue.Items.Select(item => item.Song.Id).ToArray();

        var saved = JsonSerializer.Deserialize<PlayQueue.Saved>(JsonSerializer.Serialize(queue.Save()))!;
        var reopened = PlayQueue.Restore(saved);
        Assert.True(reopened.IsPlaylist);
        Assert.True(reopened.IsShuffled);
        Assert.Equal(RepeatMode.All, reopened.Repeat);
        Assert.Equal(order, reopened.Items.Select(item => item.Song.Id));
        reopened.SetShuffle(false);
        reopened.TrimHistory(keeping: 50);
        Assert.Equal(songs, reopened.Items.Select(item => item.Song));
        Assert.Equal(songs[60], reopened.Current?.Song);
    }

    [Fact]
    public void PlaylistKeepsItsSongsWhileOldRadioHistoryIsTrimmed()
    {
        var songs = PlaylistSongs(75);
        var queue = new PlayQueue();
        queue.Replace(songs, source: Playlist);
        queue.Append(songs);
        Assert.True(queue.Jump(queue.Items[^1].Id));

        queue.TrimHistory(keeping: 2);

        Assert.Equal(songs, queue.Items.Take(songs.Length).Select(item => item.Song));
        Assert.Equal(3, queue.Items.Count(item => item.Source is null));
        Assert.Null(queue.PeekNext(automatic: true));
    }

    [Fact]
    public void UndoRestoresPlaylistOriginAndOriginalOrderAfterShuffle()
    {
        var songs = PlaylistSongs(70);
        var queue = new PlayQueue();
        queue.Replace(songs, shuffled: true, source: Playlist);
        var order = queue.Items.Select(item => item.Id).ToArray();
        var cleared = Assert.IsType<PlayQueue.ClearedUpcoming>(queue.ClearUpcoming());
        Assert.Equal(0, queue.UpcomingCount);

        Assert.True(queue.RestoreUpcoming(cleared));
        Assert.Equal(order, queue.Items.Select(item => item.Id));
        Assert.All(queue.Items, item => Assert.Equal(Playlist, item.Source));
        queue.SetShuffle(false);
        Assert.Equal(songs, queue.Items.Select(item => item.Song));
    }

    [Fact]
    public void UndoCannotOverwriteANewerQueueEdit()
    {
        var songs = PlaylistSongs(5);
        var queue = new PlayQueue();
        queue.Replace(songs, source: Playlist);
        var cleared = Assert.IsType<PlayQueue.ClearedUpcoming>(queue.ClearUpcoming());
        queue.Append([songs[2]]);

        Assert.False(queue.RestoreUpcoming(cleared));
        Assert.Equal(songs[2], Assert.Single(queue.Upcoming).Song);
    }

    [Fact]
    public void QueueSnapshotReusesTracksWhenOnlyThePositionChanges()
    {
        var queue = new PlayQueue();
        queue.Replace(PlaylistSongs(1000), shuffled: true, source: Playlist);
        var initial = queue.Save();
        queue.Advance(automatic: true);
        queue.Repeat = RepeatMode.All;

        var advanced = queue.Save();
        Assert.Same(initial.Songs, advanced.Songs);
        Assert.Same(initial.OriginalOrder, advanced.OriginalOrder);
        Assert.Equal(1, advanced.Current);
        Assert.Equal(RepeatMode.All, advanced.Repeat);
        var removed = queue.Upcoming[0];
        Assert.True(queue.Remove(removed.Id));
        Assert.DoesNotContain(removed.Song, queue.Save().Songs);
        Assert.Contains(removed.Song, initial.Songs);
    }

    [Fact]
    public async Task BackgroundQueueSaveRetainsTheNewestCompleteSnapshot()
    {
        var path = Path.Combine(Fixtures.Scratch, $"queue-{Guid.NewGuid():N}.json");
        var store = new QueueStateStore(path);
        var queue = new PlayQueue { Repeat = RepeatMode.All };
        queue.Replace(PlaylistSongs(1200), shuffled: true, source: Playlist);
        var pending = new List<Task>();
        for (var index = 0; index < 60; index++)
        {
            queue.Advance(automatic: true);
            pending.Add(store.SaveAsync(new(queue.Save(), index)));
        }

        await Task.WhenAll(pending);
        var saved = Assert.IsType<QueueStateStore.State>(store.Load());
        Assert.Equal(59, saved.Position);
        Assert.Equal(60, saved.Queue.Current);
        Assert.Equal(1200, saved.Queue.Songs.Length);
        Assert.Equal(RepeatMode.All, saved.Queue.Repeat);
        Assert.False(File.Exists(path + ".tmp"));
    }

    private static readonly PlaylistSource Playlist = new("test", new PlaylistReference("PLtest", "Playlist", false));

    private static Song[] PlaylistSongs(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new Song($"song-{index}", $"Song {index}",
            "Artist", [], null, null, null, false, null))];
}
