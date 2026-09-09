using PiStation.Protocol.Models;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PiStation.App.Composition;

internal sealed record ConvertedComposerImage(string FileName, string MediaType, MemoryStream Content) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

internal static class ComposerImageConverter
{
    internal const uint MaximumEdge = 4096;
    internal const ulong MaximumSourcePixels = 100_000_000;

    public static async Task<ThreadDraft> UploadAsync(ThreadDraft draft, string fileName, string? mediaType,
        Stream source, long byteLength,
        Func<ThreadDraft, string, string?, Stream, long, CancellationToken, Task<ThreadDraft>> upload,
        CancellationToken cancellationToken = default)
    {
        // Capture the owning draft before decoding; the upload must never resolve
        // its destination from whichever conversation is selected after the await.
        using var converted = await ConvertAsync(fileName, mediaType, source, byteLength, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await upload(draft, converted?.FileName ?? fileName, converted?.MediaType ?? mediaType,
            converted?.Content ?? source, converted?.Content.Length ?? byteLength, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsHeif(string fileName, string? mediaType)
    {
        var extension = Path.GetExtension(fileName);
        var mime = mediaType?.Split(';')[0].Trim();
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".heif", StringComparison.OrdinalIgnoreCase) ||
            mime is not null && new[] { "image/heic", "image/heif", "image/heic-sequence", "image/heif-sequence" }
                .Contains(mime, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<ConvertedComposerImage?> ConvertAsync(string fileName, string? mediaType,
        Stream source, long byteLength, CancellationToken cancellationToken = default)
    {
        if (!IsHeif(fileName, mediaType)) return null;
        if (byteLength is <= 0 or > AttachmentDefaults.MaximumFileBytes)
            throw new InvalidOperationException("HEIC/HEIF files must be between 1 byte and 50 MiB.");
        try
        {
            using var input = new InMemoryRandomAccessStream();
            using var writer = input.AsStreamForWrite();
            var buffer = new byte[81920];
            long copied = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                copied += count;
                if (copied > byteLength || copied > AttachmentDefaults.MaximumFileBytes)
                    throw new InvalidOperationException("The HEIC/HEIF file changed or exceeds the 50 MiB input limit.");
                await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (copied != byteLength) throw new InvalidOperationException("The HEIC/HEIF file changed while it was being read. Try attaching it again.");
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.HeifDecoderId, input).AsTask(cancellationToken).ConfigureAwait(false);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
                (ulong)decoder.PixelWidth * decoder.PixelHeight > MaximumSourcePixels)
                throw new InvalidOperationException("The HEIC/HEIF image exceeds the 100-megapixel conversion limit.");
            var scale = Math.Min(1d, MaximumEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken).ConfigureAwait(false);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output,
                new Dictionary<string, BitmapTypedValue> { ["ImageQuality"] = new(0.9f, PropertyType.Single) })
                .AsTask(cancellationToken).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (output.Size > (ulong)AttachmentDefaults.MaximumImageBytes)
                throw new InvalidOperationException("The converted image exceeds 10 MiB. Resize the photo and attach it again.");
            output.Seek(0);
            using var reader = output.AsStreamForRead();
            var bytes = new byte[checked((int)output.Size)];
            await reader.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return new(Path.GetFileNameWithoutExtension(fileName) + ".jpg", "image/jpeg", new MemoryStream(bytes, writable: false));
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Could not convert {Path.GetFileName(fileName)}. The file may be damaged or this PC may lack the HEIF/HEVC image codec. Open it in Windows Photos to check codec support, or export it as JPEG/PNG and attach that copy. Your original file is unchanged.", exception);
        }
    }
}
