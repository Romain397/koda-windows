using System.Text.Json.Nodes;
using Koda.Core.InnerTube;
using Koda.Core.Parsing;

namespace Koda.Core;

public sealed partial class YouTubeMusic
{
    internal async Task<PlaylistSummary[]> EditablePlaylistsAsync(string videoId, CancellationToken cancellation)
    {
        // YouTube's save picker lists playlists this account can actually add to.
        var root = await _api.WebAsync("playlist/get_add_to_playlist", new JsonObject
        {
            ["videoIds"] = new JsonArray { videoId },
            ["excludeWatchLater"] = true,
        }, cancellation, Signing.Required);

        var playlists = new List<PlaylistSummary>();
        foreach (var option in root.Objects("playlistAddToOptionRenderer"))
        {
            if (option["playlistId"].NonEmpty is not { } rawId) continue;
            var id = Pages.PlaylistId(rawId);
            if (!Pages.IsEditablePlaylistId(id)) continue;
            var title = option["title"].NonEmpty ?? option["title"].Text().TrimmedOrNull()
                ?? option["playlistTitle"].NonEmpty ?? option["playlistTitle"].Text().TrimmedOrNull();
            if (title is null) continue;
            playlists.Add(new PlaylistSummary(id, title, null, null));
        }
        return [.. playlists.DistinctBy(playlist => playlist.Id)];
    }

    internal async Task<int> AddPlaylistSongsAsync(string id, IReadOnlyList<Song> songs, CancellationToken cancellation)
    {
        id = Pages.PlaylistId(id);
        if (!Pages.IsEditablePlaylistId(id)) throw new InvalidOperationException("This playlist can't be edited.");
        var requested = songs.Select(song => song.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0) return 0;

        var pending = requested.ToHashSet(StringComparer.Ordinal);
        var root = await _api.MusicAsync("browse", new JsonObject { ["browseId"] = "VL" + id }, cancellation, Signing.Required);
        if (!Pages.HasPlaylistShelf(root) && Pages.Playlist(root, id) is null) throw MusicException.BadResponse(200);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            pending.ExceptWith(Pages.PlaylistVideoIds(root));
            if (pending.Count == 0 || Pages.PlaylistContinuation(root) is not { } token) break;
            if (!seen.Add(token)) throw MusicException.BadResponse(200);
            root = await FetchPlaylistContinuationAsync(token, cancellation, Signing.Required);
        }

        var sent = 0;
        // Keep YouTube's duplicate check enabled in case another device adds a song after this read.
        foreach (var batch in requested.Where(pending.Contains).Chunk(50))
        {
            var actions = new JsonArray([.. batch.Select(videoId => new JsonObject
            {
                ["action"] = "ACTION_ADD_VIDEO",
                ["addedVideoId"] = videoId,
            })]);
            try
            {
                await EditPlaylistAsync(id, actions, cancellation);
            }
            catch (Exception error) when (sent > 0 && error is (MusicException or InvalidOperationException))
            {
                throw new InvalidOperationException("YouTube accepted the first part of the list. Refresh the playlist before trying again.", error);
            }
            sent += batch.Length;
        }
        return sent;
    }

    internal Task RemovePlaylistTrackAsync(string id, string videoId, string setVideoId, CancellationToken cancellation) =>
        EditPlaylistAsync(id, new JsonArray { new JsonObject
        {
            ["action"] = "ACTION_REMOVE_VIDEO",
            ["removedVideoId"] = videoId,
            ["setVideoId"] = setVideoId,
        } }, cancellation);

    internal Task MovePlaylistTrackAsync(string id, string setVideoId, string beforeSetVideoId, CancellationToken cancellation) =>
        EditPlaylistAsync(id, new JsonArray { new JsonObject
        {
            ["action"] = "ACTION_MOVE_VIDEO_BEFORE",
            ["setVideoId"] = setVideoId,
            ["movedSetVideoIdSuccessor"] = beforeSetVideoId,
        } }, cancellation);

    internal Task RenamePlaylistAsync(string id, string title, CancellationToken cancellation) =>
        EditPlaylistAsync(id, new JsonArray { new JsonObject
        {
            ["action"] = "ACTION_SET_PLAYLIST_NAME",
            ["playlistName"] = title,
        } }, cancellation);

    private async Task EditPlaylistAsync(string id, JsonArray actions, CancellationToken cancellation)
    {
        id = Pages.PlaylistId(id);
        if (!Pages.IsEditablePlaylistId(id)) throw new InvalidOperationException("This playlist can't be edited.");

        try
        {
            var root = await _api.MusicAsync("browse/edit_playlist", new JsonObject
            {
                ["playlistId"] = id,
                ["actions"] = actions,
            }, cancellation, Signing.Required);
            if (root["status"].NonEmpty != "STATUS_SUCCEEDED")
                throw new InvalidOperationException("YouTube Music didn't accept the playlist change. Check that you can edit this playlist.");
        }
        finally
        {
            // If the reply was lost, re-opening the playlist must still fetch its current contents.
            ClearPageCache();
        }
    }
}
