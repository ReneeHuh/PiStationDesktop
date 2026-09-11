using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32.SafeHandles;
using PiStation.Protocol.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PiStation.App.Composition;

public sealed record BrowserVideoResult(string Path, int RequestedFramesPerSecond, int EncodedFrames,
    int SourceFrames, double DurationSeconds, double EffectiveFramesPerSecond, long SizeBytes)
{
    public int FreshFrames { get; init; }
    public int RepeatedFrames => EncodedFrames - FreshFrames;
    public int DroppedSourceFrames => SourceFrames - FreshFrames;
    public double DecodeMilliseconds { get; init; }
}

/// <summary>Bounded latest-frame capture feeding the Windows MP4 encoder without a PNG-file journal.</summary>
public sealed class BrowserVideoCapture : IAsyncDisposable
{
    private readonly int _fps;
    private readonly string _path;
    private readonly CancellationTokenSource _cancel = new();
    private sealed record Frame(byte[]? Jpeg, string? Base64)
    {
        public byte[] GetBytes()
        {
            var bytes = Jpeg ?? Convert.FromBase64String(Base64!);
            if (bytes.Length is < 4 or > 4 * 1024 * 1024)
                throw new InvalidDataException("The browser recording frame exceeds 4 MiB.");
            return bytes;
        }
    }
    private readonly TaskCompletionSource<Frame> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private readonly SemaphoreSlim _samples = new(1, 1);
    private readonly SemaphoreSlim _frameAvailable = new(0, 1);
    private readonly RecordingTimer _timer;
    private Frame? _latest;
    private Frame? _decodedFrame;
    private byte[]? _decodedPixels;
    private Task? _encoding;
    private Exception? _failure;
    private volatile bool _stop;
    private int _sourceFrames;
    private int _encodedFrames;
    private int _freshFrames;
    private double _decodeMilliseconds;
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
        _timer = new RecordingTimer();
    }

    public bool HasFinished => _encoding?.IsCompleted == true;
    public Task Completion => _encoding ?? Task.CompletedTask;

    public void PushFrame(byte[] jpeg)
    {
        if (_stop || _cancel.IsCancellationRequested) return;
        if (jpeg.Length is < 4 or > 4 * 1024 * 1024) { Fail(new InvalidDataException("The browser recording frame exceeds 4 MiB.")); return; }
        PushFrame(new Frame(jpeg, null));
    }

    // The WebView callback only publishes the latest compressed frame. Decode/base64
    // work belongs to the encoder worker, and overwritten frames need no processing.
    public void PushFrame(string base64)
    {
        if (_stop || _cancel.IsCancellationRequested) return;
        if (base64.Length is < 8 or > 5_592_408) { Fail(new InvalidDataException("The browser recording frame exceeds 4 MiB.")); return; }
        PushFrame(new Frame(null, base64));
    }

    private void PushFrame(Frame frame)
    {
        Interlocked.Exchange(ref _latest, frame);
        Interlocked.Increment(ref _sourceFrames);
        _first.TrySetResult(frame);
        if (_frameAvailable.CurrentCount == 0)
        {
            try { _frameAvailable.Release(); }
            catch (SemaphoreFullException) { /* A concurrent producer already signaled the latest frame. */ }
        }
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
        await input.WriteAsync(frame.GetBytes().AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        _width = Math.Max(2u, decoder.PixelWidth & ~1u);
        _height = Math.Max(2u, decoder.PixelHeight & ~1u);
        if (_width > 1280 || _height > 1280) throw new InvalidDataException("Recording dimensions exceed 1280 pixels.");
        token.ThrowIfCancellationRequested();
        _encoding = EncodeAsync();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
    }

    private async Task EncodeAsync()
    {
        var properties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, _width, _height);
        properties.FrameRate.Numerator = (uint)_fps;
        properties.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(properties)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, args) =>
        {
            _clock.Start();
            args.Request.SetActualStartPosition(TimeSpan.Zero);
        };
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
            while (_nextSample > _clock.Elapsed)
                await _timer.DelayAsync(_nextSample - _clock.Elapsed, _cancel.Token).ConfigureAwait(false);
            if (_stop) return;
            _frameAvailable.Wait(0);
            var frame = Volatile.Read(ref _latest) ?? throw new InvalidDataException("The browser has not supplied a frame.");
            if (ReferenceEquals(frame, _decodedFrame))
            {
                // Capture and output clocks have independent phases. Give a late
                // source frame one interval to arrive instead of encoding a repeat
                // just before it arrives and dropping the following source frame.
                await _frameAvailable.WaitAsync(TimeSpan.FromSeconds(1d / _fps), _cancel.Token).ConfigureAwait(false);
                frame = Volatile.Read(ref _latest)!;
            }
            if (_stop || _clock.Elapsed >= TimeSpan.FromMinutes(2)) { _stop = true; return; }
            var timestamp = _encodedFrames == 0 ? TimeSpan.Zero : _clock.Elapsed;
            var fresh = !ReferenceEquals(frame, _decodedFrame);
            if (fresh)
            {
                var started = Stopwatch.GetTimestamp();
                using var input = new InMemoryRandomAccessStream();
                await input.WriteAsync(frame.GetBytes().AsBuffer()).AsTask(_cancel.Token).ConfigureAwait(false);
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input).AsTask(_cancel.Token).ConfigureAwait(false);
                if (decoder.PixelWidth > 1280 || decoder.PixelHeight > 1280)
                    throw new InvalidDataException("Recording dimensions exceed 1280 pixels.");
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform { ScaledWidth = _width, ScaledHeight = _height },
                    ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(_cancel.Token).ConfigureAwait(false);
                _decodedPixels = pixels.DetachPixelData();
                _decodedFrame = frame;
                _decodeMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            // A cached pixel buffer is immutable: an encoder may still hold earlier samples.
            var sample = MediaStreamSample.CreateFromBuffer(_decodedPixels!.AsBuffer(), timestamp);
            sample.Duration = TimeSpan.FromSeconds(1d / _fps);
            sample.KeyFrame = true;
            args.Request.Sample = sample;
            _lastTimestamp = timestamp;
            // Keep deadlines on the recording clock. Skip missed slots instead of
            // shifting every subsequent deadline by decode/encoder/timer lateness.
            _nextSample = TimeSpan.FromSeconds((Math.Floor(timestamp.TotalSeconds * _fps) + 1) / _fps);
            _encodedFrames++;
            if (fresh) _freshFrames++;
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
        return new(_path, _fps, _encodedFrames, _sourceFrames, duration, _freshFrames / duration, length)
        {
            FreshFrames = _freshFrames,
            DecodeMilliseconds = _decodeMilliseconds,
        };
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
        _timer.Dispose();
        if (!_saved)
        {
            try { File.Delete(_path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.TraceWarning("Could not remove abandoned browser recording: {0}", error.Message); }
        }
    }
}

// Per-recording high-resolution waits avoid the coarse Windows Task.Delay cadence
// without spinning, occupying a worker, or changing the process/system timer period.
internal sealed class RecordingTimer : WaitHandle
{
    public RecordingTimer()
    {
        SafeWaitHandle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x00100002);
        if (SafeWaitHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero) return;
        var due = -delay.Ticks;
        if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(this,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), completion, Timeout.Infinite, true);
        try { await completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
