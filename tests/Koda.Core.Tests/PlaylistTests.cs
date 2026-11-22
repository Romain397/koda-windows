using System.Net;
using System.Text.Json.Nodes;
using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class PlaylistTests
{
    [Fact]
    public void PlaylistPageReadsHeaderShelfAndContinuation()
    {
        var page = Pages.Playlist(Fixtures.Load("playlist.json"), "PLfCLUMeAUh5c");

        Assert.NotNull(page);
        Assert.Equal("Chill EDM Hits", page!.Playlist.Title);
        Assert.Null(page.Author); // the signed-out playlist carries no strapline
        Assert.Equal("144 songs • 6+ hours", page.DetailLine);
        Assert.Equal(100, page.Songs.Length); // the shelf's 101st entry is its continuation, not a track
        Assert.Equal("Faded", page.Songs[0].Title);
        Assert.Equal("60ItHLz5WEA", page.Songs[0].Id);
        Assert.NotNull(page.Continuation);
        Assert.False(page.CanEdit);
    }

    [Fact]
    public void ContinuationPageReadsAppendedItems()
    {
        var songs = Pages.PlaylistSongs(Fixtures.Load("playlist-continuation.json"));

        Assert.Equal(44, songs.Length);
        // No new token rides appended items; the shelf's own token already did its job.
        Assert.Null(Pages.PlaylistContinuation(Fixtures.Load("playlist-continuation.json")));
        Assert.All(Pages.PlaylistTracks(Fixtures.Load("playlist-continuation.json")),
            track => Assert.False(string.IsNullOrEmpty(track.SetVideoId)));
    }

    [Fact]
    public void EditablePlaylistKeepsAnIdForEachOccurrenceOfTheSameSong()
    {
        var entry = Fixtures.Load("playlist.json").First("musicPlaylistShelfRenderer")["contents"][0];
        var first = JsonNode.Parse(entry.Raw.GetRawText())!;
        var second = first.DeepClone();
        first["musicResponsiveListItemRenderer"]!["playlistItemData"]!["playlistSetVideoId"] = "occurrence-1";
        second["musicResponsiveListItemRenderer"]!["playlistItemData"]!["playlistSetVideoId"] = "occurrence-2";
        var root = Json.Parse(new JsonObject
        {
            ["musicEditablePlaylistDetailHeaderRenderer"] = JsonNode.Parse("""
                {"playlistId":"PLowned","header":{"musicResponsiveHeaderRenderer":{"title":{"simpleText":"My playlist"}}}}
                """),
            ["musicPlaylistShelfRenderer"] = new JsonObject { ["contents"] = new JsonArray(first, second) },
        }.ToJsonString());

        var page = Assert.IsType<PlaylistPage>(Pages.Playlist(root, "PLowned"));
        Assert.True(page.CanEdit);
        Assert.Equal(page.Tracks[0].Song.Id, page.Tracks[1].Song.Id);
        Assert.Equal(["occurrence-1", "occurrence-2"], page.Tracks.Select(track => track.SetVideoId));
        Assert.False(Pages.Playlist(root, "PLdifferent")!.CanEdit);
    }

    [Fact]
    public async Task PlaylistEditsUseTheSignedAccountAndReportOnlyAcceptedWrites()
    {
        var handler = new PlaylistHandler();
        using var music = TestMusic(handler);
        using var account = new YouTubeAccount(music, new Library(null), new Settings(), new TestSecretStore(), Fixtures.Scratch);
        var session = account.PlaylistSession!;
        var edited = new List<string>();
        account.PlaylistEdited += edited.Add;
        var song = Pages.PlaylistSongs(Fixtures.Load("playlist.json"))[0];

        var playlists = await account.EditablePlaylistsAsync(session, song.Id);
        Assert.Equal("PLowned", Assert.Single(playlists).Id);
        Assert.Equal(1, await account.AddPlaylistSongsAsync(session, "VLPLowned", [song, song]));
        await account.RemovePlaylistTrackAsync(session, "PLowned", song.Id, "occurrence-2");
        await account.MovePlaylistTrackAsync(session, "PLowned", "occurrence-1", "occurrence-3");
        await account.RenamePlaylistAsync(session, "PLowned", "Changed");
        Assert.Equal(["PLowned", "PLowned", "PLowned", "PLowned"], edited);
        Assert.All(handler.Writes, request => Assert.Equal("PLowned", request["playlistId"]!.GetValue<string>()));
        var actions = handler.Writes.Select(request => Assert.Single(request["actions"]!.AsArray())!).ToArray();
        Assert.Equal("ACTION_ADD_VIDEO", actions[0]["action"]!.GetValue<string>());
        Assert.Null(actions[0]["dedupeOption"]);
        Assert.Equal("ACTION_REMOVE_VIDEO", actions[1]["action"]!.GetValue<string>());
        Assert.Equal("occurrence-2", actions[1]["setVideoId"]!.GetValue<string>());
        Assert.Equal("ACTION_MOVE_VIDEO_BEFORE", actions[2]["action"]!.GetValue<string>());
        Assert.Equal("occurrence-3", actions[2]["movedSetVideoIdSuccessor"]!.GetValue<string>());
        Assert.Equal("Changed", actions[3]["playlistName"]!.GetValue<string>());

        handler.Reject = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.RenamePlaylistAsync(session, "PLowned", "Rejected"));
        Assert.Equal(4, edited.Count);

        music.Accounts.Deactivate();
        var error = await Assert.ThrowsAsync<MusicException>(() => account.AddPlaylistSongsAsync(session, "PLowned", [song]));
        Assert.Equal(MusicException.Reason.SignedOut, error.Kind);
        Assert.Equal(5, handler.Writes.Count);
    }

    [Fact]
    public async Task AddSkipsTracksAlreadyPresentOnALaterPageEvenWhenARecordIsUnavailable()
    {
        var songs = Pages.PlaylistSongs(Fixtures.Load("playlist.json"));
        var existing = songs[0];
        var fresh = songs[1];
        var handler = new PlaylistHandler
        {
            Read = payload => payload["continuation"] is null
                ? """{"musicPlaylistShelfRenderer":{"contents":[],"continuations":[{"nextContinuationData":{"continuation":"more"}}]}}"""
                : $$$$"""
                    {"appendContinuationItemsAction":{"continuationItems":[{"musicResponsiveListItemRenderer":
                    {"playlistItemData":{"videoId":"{{{{existing.Id}}}}"}}}]}}
                    """,
        };
        using var music = TestMusic(handler);
        using var account = new YouTubeAccount(music, new Library(null), new Settings(), new TestSecretStore(), Fixtures.Scratch);
        var session = account.PlaylistSession!;
        var edits = new List<string>();
        account.PlaylistEdited += edits.Add;

        Assert.Equal(0, await account.AddPlaylistSongsAsync(session, "PLowned", [existing]));
        Assert.Empty(handler.Writes);
        Assert.Empty(edits);

        Assert.Equal(1, await account.AddPlaylistSongsAsync(session, "PLowned", [existing, fresh, fresh]));
        var write = Assert.Single(handler.Writes);
        Assert.Equal(fresh.Id, Assert.Single(write["actions"]!.AsArray())!["addedVideoId"]!.GetValue<string>());
        Assert.Equal(["PLowned"], edits);
        Assert.Equal(4, handler.PlaylistReads);
    }

    [Fact]
    public async Task AddStopsWithoutWritingIfThePlaylistCannotBeReadCompletely()
    {
        var handler = new PlaylistHandler
        {
            Read = _ => """{"musicPlaylistShelfRenderer":{"contents":[],"continuations":[{"nextContinuationData":{"continuation":"repeat"}}]}}""",
        };
        using var music = TestMusic(handler);
        var song = Pages.PlaylistSongs(Fixtures.Load("playlist.json"))[0];

        await Assert.ThrowsAsync<MusicException>(() => music.AddPlaylistSongsAsync("PLowned", [song], default));
        Assert.Empty(handler.Writes);
        Assert.Equal(2, handler.PlaylistReads);
    }

    [Fact]
    public async Task FullPlaylistWalkContinuesPastAnEmptyPageAndAThousandTracks()
    {
        var rows = Fixtures.Load("playlist.json").First("musicPlaylistShelfRenderer")["contents"].Array.Take(100);
        var handler = new PlaylistHandler
        {
            Read = payload =>
            {
                var page = payload["continuation"] is { } token ? int.Parse(token.GetValue<string>()) : 0;
                var shelf = new JsonObject
                {
                    ["contents"] = page == 1 ? new JsonArray()
                        : new JsonArray([.. rows.Select(row => JsonNode.Parse(row.Raw.GetRawText()))]),
                };
                if (page < 11)
                    shelf["continuations"] = new JsonArray { new JsonObject
                    {
                        ["nextContinuationData"] = new JsonObject { ["continuation"] = (page + 1).ToString() },
                    } };
                return new JsonObject { ["musicPlaylistShelfRenderer"] = shelf }.ToJsonString();
            },
        };
        using var music = TestMusic(handler);

        var songs = await music.PlaylistSongsAsync("VLPLowned", limit: null);

        Assert.Equal(1100, songs.Length);
        Assert.Equal(12, handler.PlaylistReads);
    }

    private static YouTubeMusic TestMusic(HttpMessageHandler handler)
    {
        var sessions = new AccountSessions();
        sessions.Activate(new AccountSession(Guid.NewGuid(), YouTubeCookies.FromHeader("SID=test-sid; SAPISID=test-sapisid")));
        var visitor = Path.Combine(Fixtures.Scratch, $"playlist-visitor-{Guid.NewGuid():N}.json");
        return new YouTubeMusic(http: new HttpClient(handler), accounts: sessions, visitorStorePath: visitor,
            signatureStorePath: visitor + ".signature");
    }

    private sealed class PlaylistHandler : HttpMessageHandler
    {
        internal List<JsonObject> Writes { get; } = [];
        internal bool Reject { get; set; }
        internal int PlaylistReads { get; private set; }
        internal Func<JsonObject, string>? Read { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = """{"responseContext":{"visitorData":"TESTTOKEN%3D%3D"}}""";
            if (path.EndsWith("/playlist/get_add_to_playlist"))
            {
                Assert.True(request.Headers.Contains("Authorization"));
                response = """
                    {"contents":[{"addToPlaylistRenderer":{"playlists":[
                      {"playlistAddToOptionRenderer":{"playlistId":"WL","title":{"simpleText":"Watch later"}}},
                      {"playlistAddToOptionRenderer":{"playlistId":"VLPLowned","title":{"runs":[{"text":"My playlist"}]}}}
                    ]}}]}
                    """;
            }
            else if (path.EndsWith("/browse"))
            {
                Assert.True(request.Headers.Contains("Authorization"));
                PlaylistReads++;
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                response = Read?.Invoke(payload) ?? """{"musicPlaylistShelfRenderer":{"contents":[]}}""";
            }
            else if (path.EndsWith("/browse/edit_playlist"))
            {
                Assert.True(request.Headers.Contains("Authorization"));
                Writes.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
                response = Reject ? """{"status":"STATUS_FAILED"}""" : """{"status":"STATUS_SUCCEEDED"}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    [Fact]
    public void EmptyRootParsesAsNoPage()
    {
        Assert.Null(Pages.Playlist(Json.Parse("{}"), "VLx"));
        Assert.Null(Pages.Album(Json.Parse("{}"), "MPREb_x"));
        Assert.Empty(Pages.PlaylistSongs(Json.Parse("{}")));
        Assert.Null(Pages.PlaylistContinuation(Json.Parse("{}")));
    }
}
