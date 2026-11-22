namespace Koda.Core;

/// <summary>A durable reference to either a device playlist or a YouTube Music playlist.</summary>
public sealed record PlaylistReference(string Id, string Title, bool IsLocal, Artwork? Artwork = null)
{
    public static PlaylistReference From(UserPlaylist playlist) => new(playlist.Id.ToString(), playlist.Name, true,
        playlist.Songs.FirstOrDefault(song => song.Artwork is not null)?.Artwork);

    public static PlaylistReference From(PlaylistSummary playlist) => new(YouTubeId(playlist.Id), playlist.Title, false, playlist.Artwork);

    public static string YouTubeId(string id) => Parsing.Pages.PlaylistId(id);

    public bool SamePlaylist(PlaylistReference other) => Id == other.Id && IsLocal == other.IsLocal;
}
