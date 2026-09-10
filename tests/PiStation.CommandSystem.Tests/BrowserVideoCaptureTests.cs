using System.Runtime.InteropServices.WindowsRuntime;
using PiStation.App.Composition;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PiStation.CommandSystem.Tests;

public sealed class BrowserVideoCaptureTests
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task WindowsEncoderProducesPlayableTimedMp4AtBothRequestedRates(int fps)
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-video-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? saved = null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            var pixels = Enumerable.Repeat((byte)100, 320 * 180 * 4).ToArray();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 320, 180, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            var bytes = new byte[(int)stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            await using var capture = new BrowserVideoCapture(root, fps);
            capture.PushFrame(bytes);
            await capture.StartAsync(CancellationToken.None);
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(2))
            {
                capture.PushFrame(bytes);
                await Task.Delay(TimeSpan.FromSeconds(1d / fps));
            }
            var result = await capture.StopAsync(CancellationToken.None);
            saved = result.Path;
            Assert.Equal(fps, result.RequestedFramesPerSecond);
            Assert.InRange(result.DurationSeconds, 1.5, 10);
            Assert.True(result.EncodedFrames >= 10);
            Assert.InRange(result.EffectiveFramesPerSecond, 1, fps + 2);
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(saved));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 1.5, 10);
            Assert.Equal(result.SizeBytes, new FileInfo(saved).Length);
        }
        finally
        {
            if (saved is not null) File.Delete(saved);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
    }

    [Fact]
    public async Task CancelBeforeFirstFrameLeavesNoArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-video-tests", Guid.NewGuid().ToString("N"));
        await using (var capture = new BrowserVideoCapture(root, 30))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StartAsync(canceled.Token));
        }
        Assert.Empty(Directory.GetFiles(root));
        Directory.Delete(root);
    }
}
