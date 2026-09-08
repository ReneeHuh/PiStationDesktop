using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiStation.ClientRuntime;

public sealed record RecoveredFile(string Context, string RelativePath, string Content, string Revision);
public sealed record RecoveredDraft(string ThreadId, string DraftId, string BaseText, string Text);

/// <summary>Device-local recovery copies. Only explicit saves/discards remove unsaved work.</summary>
public sealed class EditingRecoveryStore
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]?> _pending = new(StringComparer.Ordinal);
    private Task _writer = Task.CompletedTask;
    private bool _writing;
    private Exception? _failure;

    public EditingRecoveryStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public void SaveFile(RecoveredFile file) => Queue(FilePath(file.Context, file.RelativePath),
        JsonSerializer.SerializeToUtf8Bytes(file, EditingRecoveryJsonContext.Default.RecoveredFile));

    public void RemoveFile(string context, string relativePath) => Queue(FilePath(context, relativePath), null);

    public IReadOnlyList<RecoveredFile> LoadFiles(string context)
    {
        var prefix = "file-" + Hash(context) + "-";
        string[] paths;
        lock (_gate)
            paths = Directory.EnumerateFiles(_directory, prefix + "*.protected")
                .Concat(_pending.Keys.Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal).ToArray();
        return paths.Select(Read).Where(bytes => bytes is not null)
            .Select(bytes => JsonSerializer.Deserialize(bytes!, EditingRecoveryJsonContext.Default.RecoveredFile)
                ?? throw new InvalidDataException("A recovered file is invalid."))
            .Where(file => file.Context == context).ToArray();
    }

    public void SaveDraft(RecoveredDraft draft) => Queue(DraftPath(draft.ThreadId),
        JsonSerializer.SerializeToUtf8Bytes(draft, EditingRecoveryJsonContext.Default.RecoveredDraft));

    public void RemoveDraft(string threadId) => Queue(DraftPath(threadId), null);

    public RecoveredDraft? LoadDraft(string threadId) => Read(DraftPath(threadId)) is { } bytes
        ? JsonSerializer.Deserialize(bytes, EditingRecoveryJsonContext.Default.RecoveredDraft) : null;

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Task writer;
        lock (_gate)
        {
            StartWriter();
            writer = _writer;
        }
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
            if (_failure is not null)
                throw new IOException("Local recovery could not be saved. Keep this window open and free disk space or restore access to app data.", _failure);
    }

    private byte[]? Read(string path)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var pending)) return pending;
            if (!File.Exists(path)) return null;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Editing recovery uses Windows protected storage.");
            return WindowsProtectedStorage.Read(path);
        }
    }

    private void Queue(string path, byte[]? bytes)
    {
        lock (_gate)
        {
            _pending[path] = bytes;
            StartWriter();
        }
    }

    private void StartWriter()
    {
        if (_writing || _pending.Count == 0) return;
        _writing = true;
        _failure = null;
        _writer = Task.Run(WritePending);
    }

    private void WritePending()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_pending.Count == 0) { _writing = false; return; }
                var entry = _pending.First();
                // Keep reads ordered with writes and never expose a gap while replacing a copy.
                try
                {
                    if (entry.Value is null) File.Delete(entry.Key);
                    else
                    {
                        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Editing recovery uses Windows protected storage.");
                        WindowsProtectedStorage.Write(entry.Key, entry.Value);
                    }
                    _pending.Remove(entry.Key);
                }
                catch (Exception exception)
                {
                    _failure = exception;
                    _writing = false;
                    return;
                }
            }
        }
    }

    private string FilePath(string context, string relativePath) =>
        Path.Combine(_directory, "file-" + Hash(context) + "-" + Hash(relativePath.ToUpperInvariant()) + ".protected");
    private string DraftPath(string threadId) => Path.Combine(_directory, "draft-" + Hash(threadId) + ".protected");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

[JsonSerializable(typeof(RecoveredFile))]
[JsonSerializable(typeof(RecoveredDraft))]
internal sealed partial class EditingRecoveryJsonContext : JsonSerializerContext;
