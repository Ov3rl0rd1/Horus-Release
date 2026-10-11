using System.Runtime.Versioning;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Horus.Platforms.Windows
{
    /// <summary>
    /// An executable's icon, as Windows shows it in Explorer, saved as a PNG the UI can load.
    ///
    /// <para>The shell's thumbnail of an .exe is its icon, and asking for it through WinRT
    /// needs no GDI and no System.Drawing. Re-encoded to PNG because the thumbnail stream's
    /// own format is unspecified, and the cache file is what an Image binds to.</para>
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    internal static class ExeIcons
    {
        private const uint Size = 48;

        /// <returns>The PNG's path, or null when Windows had no icon to give.</returns>
        public static async Task<string?> ExtractAsync(string exePath, string pngPath, CancellationToken ct)
        {
            if (File.Exists(pngPath)) return pngPath;

            var file = await StorageFile.GetFileFromPathAsync(exePath).AsTask(ct);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, Size, ThumbnailOptions.ResizeThumbnail).AsTask(ct);
            if (thumbnail is null || thumbnail.Size == 0) return null;

            var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(ct);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);

            using var memory = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory).AsTask(ct);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(ct);

            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
            var bytes = new byte[memory.Size];
            memory.Seek(0);
            using (var reader = new DataReader(memory.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)memory.Size).AsTask(ct);
                reader.ReadBytes(bytes);
            }

            // Written to a temp name first: the list may already be trying to show this file.
            var temp = pngPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, pngPath, overwrite: true);
            return pngPath;
        }
    }
}
