using System.Security.Cryptography;
using System.Text;

namespace Koda.Core;

/// <summary>Custom playlist artwork belongs to a Windows profile and is keyed by playlist identity.</summary>
public sealed class PlaylistCoverStore(string profileFolder)
{
    private readonly string _folder = Path.Combine(profileFolder, "playlist-covers");

    public string? Find(PlaylistReference playlist)
    {
        var path = PathFor(playlist);
        return File.Exists(path) ? path : null;
    }

    public void Save(PlaylistReference playlist, byte[] png)
    {
        Directory.CreateDirectory(_folder);
        var path = PathFor(playlist);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Remove(PlaylistReference playlist)
    {
        if (Find(playlist) is { } path) File.Delete(path);
    }

    internal void RemoveAll()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string PathFor(PlaylistReference playlist)
    {
        var kind = playlist.IsLocal ? "local" : "youtube";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(playlist.Id)));
        return Path.Combine(_folder, $"{kind}-{hash}.png");
    }
}
