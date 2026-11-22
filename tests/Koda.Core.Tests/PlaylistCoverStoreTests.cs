namespace Koda.Core.Tests;

public class PlaylistCoverStoreTests
{
    [Fact]
    public void CoversFollowProfileAndPlaylistIdentityAndCanBeReplaced()
    {
        var directory = Path.Combine(Fixtures.Scratch, $"covers-{Guid.NewGuid():N}");
        var store = new PlaylistCoverStore(directory);
        var otherProfile = new PlaylistCoverStore(Path.Combine(directory, "other-profile"));
        var source = new PlaylistReference("PLtest", "Old title", false);
        var renamed = source with { Title = "New title" };
        var local = source with { IsLocal = true };
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6];

        Assert.Null(store.Find(source));
        store.Save(source, first);
        var file = new PlaylistCoverStore(directory).Find(renamed);
        Assert.NotNull(file);
        Assert.Equal(first, File.ReadAllBytes(file));
        Assert.Null(store.Find(local));
        Assert.Null(otherProfile.Find(source));

        store.Save(renamed, second);
        Assert.Equal(file, store.Find(source));
        Assert.Equal(second, File.ReadAllBytes(file));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(file)!));

        store.Remove(source);
        Assert.Null(store.Find(renamed));
    }

    [Fact]
    public void DeletingALocalPlaylistRemovesItsCover()
    {
        var folder = Path.Combine(Fixtures.Scratch, $"covers-delete-{Guid.NewGuid():N}");
        var library = new Library(folder);
        var store = new PlaylistCoverStore(folder);
        var playlist = library.CreatePlaylist("Test playlist");
        var reference = PlaylistReference.From(playlist);
        store.Save(reference, [1, 2, 3]);

        library.DeletePlaylist(playlist.Id);

        Assert.Null(store.Find(reference));
    }
}
