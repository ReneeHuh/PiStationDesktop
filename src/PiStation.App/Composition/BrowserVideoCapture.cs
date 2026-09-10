using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PiStation.Protocol.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PiStation.App.Composition;

public sealed record BrowserVideoResult(string Path, int RequestedFramesPerSecond, int EncodedFrames,
    int SourceFrames, double DurationSeconds, double EffectiveFramesPerSecond, long SizeBytes);

/// <summary>Bounded latest-frame capture feeding the Windows MP4 encoder without a PNG-file journal.</summary>
public sealed class BrowserVideoCapture : IAsyncDisposable
{
    private readonly int _fps;
    private readonly string _path;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<byte[]> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private readonly SemaphoreSlim _samples = new(1, 1);
    private byte[]? _latest;
    private Task? _encoding;
    private Exception? _failure;
    private volatile bool _stop;
    private int _sourceFrames;
    private int _encodedFrames;
    private TimeSpan _lastTimestamp;
    private TimeSpan _nextSample;
    private uint _width;
    private uint _height;
    private bool _saved;

    public BrowserVideoCapture(string directory, int framesPerSecond)
    {
        if (framesPerSecond is not (30 or 60)) throw new ArgumentException("Choose 30 or 60 FPS.", nameof(framesPerSecond));
        _fps = framesPerSecond;
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "preview-" + Guid.NewGuid().ToString("N") + ".mp4");
    }

    public bool HasFinished => _encoding?.IsCompleted == true;
    public Task Completion => _encoding ?? Task.CompletedTask;

    public void PushFrame(byte[] jpeg)
    {
        if (_stop || _cancel.IsCancellationRequested) return;
        if (jpeg.Length is < 4 or > 4 * 1024 * 1024) { Fail(new InvalidDataException("The browser recording frame exceeds 4 MiB.")); return; }
        Interlocked.Exchange(ref _latest, jpeg);
        Interlocked.Increment(ref _sourceFrames);
        _first.TrySetResult(jpeg);
    }

    public void Fail(Exception error)
    {
        _failure ??= error;
        _first.TrySetException(error);
        _stop = true;
    }

    public async Task StartAsync(CancellationToken token)
    {
        var frame = await _first.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(frame.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        _width = Math.Max(2u, decoder.PixelWidth & ~1u);
        _height = Math.Max(2u, decoder.PixelHeight & ~1u);
        if (_width > 1280 || _height > 1280) throw new InvalidDataException("Recording dimensions exceed 1280 pixels.");
        token.ThrowIfCancellationRequested();
        _clock.Start();
        _encoding = EncodeAsync();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
    }

    private async Task EncodeAsync()
    {
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, _width, _height);
        properties.FrameRate.Numerator = (uint)_fps;
        properties.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(properties)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(CreateOutput());
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Audio = null;
            profile.Video.Width = _width;
            profile.Video.Height = _height;
            profile.Video.FrameRate.Numerator = (uint)_fps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = 3_000_000;
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var preparation = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile).AsTask(_cancel.Token);
            if (!preparation.CanTranscode) throw new InvalidOperationException($"The Windows video encoder is unavailable ({preparation.FailureReason}).");
            _ready.TrySetResult();
            await preparation.TranscodeAsync().AsTask(_cancel.Token);
            if (_failure is not null) throw new InvalidOperationException("The recording could not be completed.", _failure);
        }
        catch (Exception error) { _ready.TrySetException(error); throw; }
        finally { source.SampleRequested -= OnSampleRequested; }
    }

    private string CreateOutput()
    {
        using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return _path;
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        var entered = false;
        try
        {
            await _samples.WaitAsync(_cancel.Token).ConfigureAwait(false);
            entered = true;
            if (_stop || _clock.Elapsed >= TimeSpan.FromMinutes(2)) { _stop = true; return; }
            var wait = _nextSample - _clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _cancel.Token).ConfigureAwait(false);
            if (_stop) return;
            var jpeg = Volatile.Read(ref _latest) ?? throw new InvalidDataException("The browser has not supplied a frame.");
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(jpeg.AsBuffer()).AsTask(_cancel.Token).ConfigureAwait(false);
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(_cancel.Token).ConfigureAwait(false);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = _width, ScaledHeight = _height },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(_cancel.Token).ConfigureAwait(false);
            var timestamp = _encodedFrames == 0 ? TimeSpan.Zero : _clock.Elapsed;
            var sample = MediaStreamSample.CreateFromBuffer(pixels.DetachPixelData().AsBuffer(), timestamp);
            sample.Duration = TimeSpan.FromSeconds(1d / _fps);
            sample.KeyFrame = true;
            args.Request.Sample = sample;
            _lastTimestamp = timestamp;
            _nextSample += TimeSpan.FromSeconds(1d / _fps);
            if (_nextSample < _clock.Elapsed) _nextSample = _clock.Elapsed;
            _encodedFrames++;
            if (new FileInfo(_path).Length > BrowserAutomationLimits.MaximumRecordingBytes - 1024 * 1024)
                _stop = true;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally { if (entered) _samples.Release(); deferral.Complete(); }
    }

    public async Task<BrowserVideoResult> StopAsync(CancellationToken token)
    {
        _stop = true;
        if (_encoding is null) throw new InvalidOperationException("The recording has not started.");
        await _encoding.WaitAsync(TimeSpan.FromSeconds(25), token);
        var length = new FileInfo(_path).Length;
        if (_encodedFrames == 0 || length is < 12 or > BrowserAutomationLimits.MaximumRecordingBytes)
            throw new InvalidDataException("The recording is empty or exceeds 64 MiB.");
        _saved = true;
        var duration = _lastTimestamp.TotalSeconds + 1d / _fps;
        return new(_path, _fps, _encodedFrames, _sourceFrames, duration, Math.Min(_encodedFrames, _sourceFrames) / duration, length);
    }

    public async ValueTask DisposeAsync()
    {
        _stop = true;
        await _cancel.CancelAsync();
        _first.TrySetCanceled();
        if (_encoding is not null)
        {
            try { await _encoding; }
            catch (Exception) { /* The caller observes StopAsync; abandoned encodes are discarded. */ }
        }
        _cancel.Dispose();
        if (!_saved)
        {
            try { File.Delete(_path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Could not remove abandoned browser recording: {0}", error.Message); }
        }
    }
}
