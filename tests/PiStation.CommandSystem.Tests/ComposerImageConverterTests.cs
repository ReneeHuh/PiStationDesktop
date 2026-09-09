using PiStation.App.Composition;
using PiStation.Protocol.Models;
using PiStation.Protocol.Identifiers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PiStation.CommandSystem.Tests;

public sealed class ComposerImageConverterTests
{
    [Theory]
    [InlineData("photo.HEIC", "", true)]
    [InlineData("photo.heif", null, true)]
    [InlineData("photo", "IMAGE/HEIC; charset=binary", true)]
    [InlineData("photo.bin", "image/heif-sequence", true)]
    [InlineData("photo.png", "image/png", false)]
    public void RecognizesFilenameAndMimeType(string name, string? mediaType, bool expected) =>
        Assert.Equal(expected, ComposerImageConverter.IsHeif(name, mediaType));

    [Fact]
    public async Task OtherAttachmentsAreNotReadOrChanged()
    {
        using var source = new MemoryStream([1, 2, 3]);
        Assert.Null(await ComposerImageConverter.ConvertAsync("document.txt", "text/plain", source, source.Length));
        Assert.Equal(0, source.Position);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task RejectsOversizedInputBeforeReadingAndHonorsCancellation()
    {
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerImageConverter.ConvertAsync(
            "photo.heic", null, source, AttachmentDefaults.MaximumFileBytes + 1));
        Assert.Equal(0, source.Position);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComposerImageConverter.ConvertAsync(
            "photo.heic", null, source, source.Length, canceled.Token));
    }

    [Fact]
    public async Task CorruptInputReportsConversionRecoveryWithoutClosingOriginalStream()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerImageConverter.ConvertAsync(
            "damaged.heic", null, source, source.Length));
        Assert.Contains("JPEG/PNG", exception.Message);
        Assert.True(source.CanRead);
        Assert.Equal([1, 2, 3, 4], source.ToArray());
    }

    [Fact]
    public async Task ChangedLengthIsRejectedBeforeDecoding()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ComposerImageConverter.ConvertAsync(
            "photo.heic", null, source, 3));
        Assert.Contains("changed", exception.Message);
    }

    [HeifCodecTheory]
    [InlineData(64u, 32u, 1)]
    [InlineData(4608u, 64u, 1)]
    [InlineData(64u, 32u, 6)]
    public async Task RealHeifImageConvertsToJpegWithCorrectNameMimeAndDimensions(uint width, uint height, ushort orientation)
    {
        using var input = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, input);
        var pixels = Enumerable.Range(0, checked((int)(width * height))).SelectMany(_ => new byte[] { 30, 80, 180, 255 }).ToArray();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        if (orientation != 1)
            await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, BitmapTypedValue>
                { ["System.Photo.Orientation"] = new(orientation, Windows.Foundation.PropertyType.UInt16) });
        await encoder.FlushAsync();
        input.Seek(0);
        using var source = input.AsStreamForRead();
        using var converted = await ComposerImageConverter.ConvertAsync("photo.HEIC", "image/heic", source, (long)input.Size);
        Assert.NotNull(converted);
        Assert.Equal("photo.jpg", converted.FileName);
        Assert.Equal("image/jpeg", converted.MediaType);
        Assert.Equal(0, converted.Content.Position);
        using var jpeg = converted.Content.AsRandomAccessStream();
        var decoded = await BitmapDecoder.CreateAsync(jpeg);
        Assert.Equal(BitmapDecoder.JpegDecoderId, decoded.DecoderInformation.CodecId);
        var scale = Math.Min(1d, ComposerImageConverter.MaximumEdge / (double)Math.Max(width, height));
        var expectedWidth = (uint)Math.Round(width * scale);
        var expectedHeight = (uint)Math.Round(height * scale);
        Assert.Equal(orientation == 6 ? expectedHeight : expectedWidth, decoded.PixelWidth);
        Assert.Equal(orientation == 6 ? expectedWidth : expectedHeight, decoded.PixelHeight);
    }

    [HeifCodecTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedConversionRetainsOriginalDraftAndCancellationNeverUploads(bool cancel)
    {
        using var image = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, image);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 16, 16, 96, 96, new byte[16 * 16 * 4]);
        await encoder.FlushAsync();
        image.Seek(0);
        using var imageReader = image.AsStreamForRead();
        var bytes = new byte[(int)image.Size];
        await imageReader.ReadExactlyAsync(bytes);
        using var delayed = new DelayedReadStream(bytes);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var original = new ThreadDraft(EnvironmentId.New(), ThreadId.New(), DraftId.New(), "Original text", 2,
            DateTimeOffset.UtcNow, [], [new("context", "quote", "Quoted text", "Keep this context")]);
        var selectedDraft = original;
        ThreadDraft? uploaded = null;
        var operation = ComposerImageConverter.UploadAsync(selectedDraft, "photo.heic", "image/heic", delayed, delayed.Length,
            async (owner, name, mime, content, length, token) =>
            {
                uploaded = owner;
                Assert.Equal("photo.jpg", name);
                Assert.Equal("image/jpeg", mime);
                Assert.Equal(length, content.Length);
                var header = new byte[2];
                await content.ReadExactlyAsync(header, token);
                Assert.Equal(new byte[] { 0xff, 0xd8 }, header);
                return owner;
            }, cancellation.Token);
        await delayed.Started.Task.WaitAsync(cancellation.Token);
        selectedDraft = original with { ThreadId = ThreadId.New(), DraftId = DraftId.New(), Text = "Other conversation" };
        if (cancel) cancellation.Cancel();
        delayed.Resume.TrySetResult();
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
            Assert.Null(uploaded);
        }
        else
        {
            Assert.Equal(original, await operation);
            Assert.Same(original, uploaded);
            Assert.NotEqual(selectedDraft.ThreadId, uploaded!.ThreadId);
            Assert.Equal(original.Context, uploaded.Context);
            Assert.Equal("Original text", uploaded.Text);
        }
    }

    private sealed class DelayedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

internal sealed class HeifCodecTheoryAttribute : TheoryAttribute
{
    public HeifCodecTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("PISTATION_TEST_HEIF_CODEC") != "1")
            Skip = "Set PISTATION_TEST_HEIF_CODEC=1 on a Windows machine with HEIF/HEVC encoding and decoding support.";
    }
}
