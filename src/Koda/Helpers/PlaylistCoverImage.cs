using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Koda.Helpers;

internal static class PlaylistCoverImage
{
    internal static async Task<byte[]> EncodeAsync(IRandomAccessStream input)
    {
        if (input.Size > 25 * 1024 * 1024)
            throw new ArgumentException("Choose an image under 25 MB.");

        var decoder = await BitmapDecoder.CreateAsync(input);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        if (width == 0 || height == 0 || width > 16_384 || height > 16_384 || (ulong)width * height > 48_000_000)
            throw new ArgumentException("Choose a smaller image.");

        var shortSide = Math.Min(width, height);
        var side = Math.Min(512u, shortSide);
        var scaledWidth = (uint)Math.Round((double)width * side / shortSide);
        var scaledHeight = (uint)Math.Round((double)height * side / shortSide);
        var rotated = decoder.OrientedPixelWidth != width;
        var orientedWidth = rotated ? scaledHeight : scaledWidth;
        var orientedHeight = rotated ? scaledWidth : scaledHeight;
        var transform = new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds
            {
                X = (orientedWidth - side) / 2,
                Y = (orientedHeight - side) / 2,
                Width = side,
                Height = side,
            },
        };

        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        using var reader = new DataReader(output.GetInputStreamAt(0));
        var size = (uint)output.Size;
        if (await reader.LoadAsync(size) != size) throw new InvalidOperationException("The cover could not be encoded.");
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
