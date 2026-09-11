using System.Runtime.InteropServices.WindowsRuntime;
using PiStation.App.Composition;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PiStation.CommandSystem.Tests;

public sealed class BrowserVideoCaptureTests(Xunit.Abstractions.ITestOutputHelper output)
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
            Assert.Equal(result.FreshFrames / result.DurationSeconds, result.EffectiveFramesPerSecond);
            Assert.Equal(result.SourceFrames, result.FreshFrames + result.DroppedSourceFrames);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
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
    public async Task OverwrittenFramesAndRepeatedSamplesDoNotInflateFreshFrameRate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-video-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var frame = Convert.ToBase64String(await CreateFrameAsync(320, 180, 1));
            await using var capture = new BrowserVideoCapture(root, 60);
            // Only the last of this burst can reach the encoder. Received count is
            // deliberately much larger than the number of output samples.
            for (var i = 0; i < 500; i++) capture.PushFrame(frame);
            await capture.StartAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(1));
            var result = await capture.StopAsync(CancellationToken.None);
            Assert.Equal(500, result.SourceFrames);
            Assert.Equal(1, result.FreshFrames);
            Assert.Equal(499, result.DroppedSourceFrames);
            Assert.True(result.RepeatedFrames > 5);
            Assert.Equal(1 / result.DurationSeconds, result.EffectiveFramesPerSecond);
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(result.Path));
            Assert.InRange(clip.OriginalDuration.TotalSeconds, .5, 5);
            File.Delete(result.Path);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        }
        finally { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
    }

    [Theory]
    [InlineData(30, 1280, 720)]
    [InlineData(60, 1280, 720)]
    [InlineData(30, 1280, 1280)]
    [InlineData(60, 1280, 1280)]
    public async Task MovingFramesAtCaptureLimitRemainPlayableAndReportMeasuredThroughput(int fps, uint width, uint height)
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-video-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var frames = new string[16];
            for (var i = 0; i < frames.Length; i++) frames[i] = Convert.ToBase64String(await CreateFrameAsync(width, height, i));
            await using var capture = new BrowserVideoCapture(root, fps);
            capture.PushFrame(frames[0]);
            await capture.StartAsync(CancellationToken.None);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            using var sourceTimer = new RecordingTimer();
            var nextSourceFrame = 1;
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
            {
                await sourceTimer.DelayAsync(TimeSpan.FromSeconds((double)nextSourceFrame / fps) - clock.Elapsed, CancellationToken.None);
                if (clock.Elapsed >= TimeSpan.FromSeconds(3)) break;
                capture.PushFrame(frames[nextSourceFrame % frames.Length]);
                nextSourceFrame = Math.Max(nextSourceFrame + 1, (int)(clock.Elapsed.TotalSeconds * fps) + 1);
            }
            var result = await capture.StopAsync(CancellationToken.None);
            Assert.InRange(result.FreshFrames, 10, result.EncodedFrames);
            Assert.InRange(result.DurationSeconds, 2.5, 10);
            var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(result.Path));
            Assert.InRange(Math.Abs(clip.OriginalDuration.TotalSeconds - result.DurationSeconds), 0, .2);
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                width, height, result, cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                workingSetBytes = process.WorkingSet64,
            }));
            File.Delete(result.Path);
        }
        finally { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
    }

    private static async Task<byte[]> CreateFrameAsync(uint width, uint height, int phase)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        var pixels = new byte[checked((int)(width * height * 4))];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = checked((int)((y * width + x) * 4));
                pixels[offset] = (byte)((x + phase * 17) % 256);
                pixels[offset + 1] = (byte)((y + phase * 31) % 256);
                pixels[offset + 2] = (byte)(phase * 15);
                pixels[offset + 3] = 255;
            }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[(int)stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    [Fact]
    public async Task InvalidCompressedFrameFailsAndDiscardsTheRecording()
    {
        var root = Path.Combine(Path.GetTempPath(), "pistation-video-tests", Guid.NewGuid().ToString("N"));
        await using (var capture = new BrowserVideoCapture(root, 60))
        {
            capture.PushFrame(await CreateFrameAsync(320, 180, 0));
            await capture.StartAsync(CancellationToken.None);
            capture.PushFrame("this-is-not-base64");
            await Assert.ThrowsAnyAsync<Exception>(() => capture.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(capture.HasFinished);
            await Assert.ThrowsAnyAsync<Exception>(() => capture.StopAsync(CancellationToken.None));
        }
        Assert.Empty(Directory.GetFiles(root));
        Directory.Delete(root);
    }

    [Fact]
    public async Task RecordingTimerWaitsUntilDueAndCancellationDoesNotPoisonReuse()
    {
        using var timer = new RecordingTimer();
        using var cancel = new CancellationTokenSource();
        var waiting = timer.DelayAsync(TimeSpan.FromSeconds(10), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await timer.DelayAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(24));
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
