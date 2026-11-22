using System;
using System.IO;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Koda.Helpers;

internal static class PlaylistCovers
{
    internal static event Action<PlaylistReference>? Changed;

    private static PlaylistCoverStore Store => new(Profiles.ActiveFolder);

    internal static BitmapImage? ImageFor(PlaylistReference playlist, int pixels, bool customOnly = false)
    {
        if (Profiles.IsSwitching) return null;

        var path = Store.Find(playlist);
        var uri = path is not null ? new Uri(Path.GetFullPath(path)) : customOnly ? null : playlist.Artwork?.Resized(pixels * 2);
        if (uri is null) return null;

        return new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = pixels,
            CreateOptions = path is null ? BitmapCreateOptions.None : BitmapCreateOptions.IgnoreImageCache,
            UriSource = uri,
        };
    }

    internal static void AddMenuItems(MenuFlyout menu, PlaylistReference playlist)
    {
        var profile = Profiles.ActiveKey;
        var hasCover = Store.Find(playlist) is not null;
        menu.Items.Add(SongMenu.Item(hasCover ? "Change cover…" : "Choose cover…", "\uEB9F",
            () => _ = ChooseAsync(playlist, profile)));
        if (hasCover)
        {
            menu.Items.Add(SongMenu.Item("Restore default cover", "\uE7A7", () => RestoreDefault(playlist, profile)));
        }
    }

    private static async Task ChooseAsync(PlaylistReference playlist, string profile)
    {
        if (!CanEdit(playlist, profile) || App.Shell is not { } shell) return;
        var store = Store;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, shell.Handle);
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null || !CanEdit(playlist, profile)) return;

            using var input = await file.OpenAsync(FileAccessMode.Read);
            var cover = await PlaylistCoverImage.EncodeAsync(input);
            if (!CanEdit(playlist, profile)) return;
            store.Save(playlist, cover);
            Changed?.Invoke(playlist);
            shell.Note("Playlist cover updated.");
        }
        catch (ArgumentException error)
        {
            shell.Note(error.Message);
        }
        catch (Exception)
        {
            shell.Note("Couldn't use that image. Try a JPG or PNG file.");
        }
    }

    private static void RestoreDefault(PlaylistReference playlist, string profile)
    {
        if (!CanEdit(playlist, profile)) return;
        try
        {
            Store.Remove(playlist);
            Changed?.Invoke(playlist);
            App.Shell?.Note("Default playlist cover restored.");
        }
        catch (Exception)
        {
            App.Shell?.Note("Couldn't restore the default cover. Try again.");
        }
    }

    private static bool CanEdit(PlaylistReference playlist, string profile) => !Profiles.IsSwitching
        && profile == Profiles.ActiveKey && App.Library.ResolvePlaylist(playlist) is not null;
}
