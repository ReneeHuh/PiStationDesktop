using System.Security.Cryptography;
using PiStation.Protocol.Models;

namespace PiStation.Host.Preview;

public sealed partial class BrowserAutomationBridge
{
    public async Task<BrowserRecordingArtifact?> UploadRecordingAsync(string id, BrowserRecordingChunk chunk,
        string principal, string connection, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Content is not { Length: > 0 and <= BrowserAutomationLimits.RecordingChunkBytes } || chunk.Offset < 0 ||
            chunk.Offset > BrowserAutomationLimits.MaximumRecordingBytes - chunk.Content.Length)
            throw new ArgumentException("Recording chunks must be at most 64 KiB and the file at most 64 MiB.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Prune();
            var session = Require(id, principal, connection);
            if (session.Access != BrowserAutomationAccess.Interact || session.Active is not { Operation: "recording_stop" } active ||
                active.Id != chunk.RequestId || !Pending(session, active))
                throw new UnauthorizedAccessException("The recording-stop request is no longer active.");
            if (session.RecordingArtifact?.Id == chunk.RequestId)
                throw new InvalidOperationException("This recording was already finalized.");
            var directory = Path.Combine(session.Directory, "artifacts");
            SafeDirectory(directory);
            var path = Path.Combine(directory, chunk.RequestId + ".mp4");
            try
            {
                if (session.RecordingUpload is null)
                {
                    if (chunk.Offset != 0 || chunk.Content.Length < 12 || !chunk.Content.AsSpan(4, 4).SequenceEqual("ftyp"u8))
                        throw new ArgumentException("A recording must begin with an MP4 header at offset zero.");
                    session.RecordingRequestId = chunk.RequestId;
                    session.RecordingUpload = new FileStream(path + ".partial", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
                }
                var stream = session.RecordingUpload;
                if (session.RecordingRequestId != chunk.RequestId || stream.Length != chunk.Offset)
                    throw new ArgumentException("The recording upload offset does not match. The upload cannot be replayed.");
                await stream.WriteAsync(chunk.Content, token).ConfigureAwait(false);
                Renew(session);
                if (!chunk.Final) return null;
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                var length = stream.Length;
                await stream.DisposeAsync().ConfigureAwait(false);
                session.RecordingUpload = null;
                token.ThrowIfCancellationRequested();
                File.Move(path + ".partial", path, overwrite: false);
                session.RecordingArtifact = new(chunk.RequestId, path, "video/mp4", length, hash, DateTimeOffset.UtcNow);
                foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.mp4").OrderByDescending(file => file.LastWriteTimeUtc).Skip(10))
                {
                    try { if ((old.Attributes & FileAttributes.ReparsePoint) == 0) old.Delete(); }
                    catch (IOException) { /* A reader may hold an earlier artifact. Retry retention on the next recording. */ }
                    catch (UnauthorizedAccessException) { }
                }
                return session.RecordingArtifact;
            }
            catch { DiscardRecordingUpload(session); throw; }
        }
        finally { _gate.Release(); }
    }

    private static void DiscardRecordingUpload(Session session)
    {
        var stream = session.RecordingUpload;
        session.RecordingUpload = null;
        var path = session.RecordingRequestId is { } requestId
            ? Path.Combine(session.Directory, "artifacts", requestId + ".mp4.partial") : null;
        session.RecordingRequestId = null;
        stream?.Dispose();
        if (path is not null) File.Delete(path);
    }
}
