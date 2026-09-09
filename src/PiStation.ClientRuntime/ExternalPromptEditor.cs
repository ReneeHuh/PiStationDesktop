using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed record ExternalPromptEdit(Guid Id, EnvironmentId EnvironmentId, ThreadId ThreadId,
    DraftId DraftId, string OriginalText, DateTimeOffset CreatedUtc);

public sealed record ExternalEditorPreferences(string Executable, string Arguments = "");
public sealed record ExternalPromptApplyResult(bool Applied, ThreadDraft CurrentDraft, string? Message = null);
internal sealed record StoredExternalPromptEdit(Guid Id, string EnvironmentId, string ThreadId, string DraftId,
    string OriginalText, DateTimeOffset CreatedUtc);

/// <summary>Device-local editor handoffs. Closing an editor or the app never implicitly sends or replaces a prompt.</summary>
public sealed class ExternalPromptEditor
{
    public const int MaximumTextLength = 128 * 1024;
    private const int MaximumFileBytes = MaximumTextLength * 4 + 4;
    private readonly string _root;

    public ExternalPromptEditor(string directory)
    {
        _root = Path.GetFullPath(directory);
        Directory.CreateDirectory(_root);
        EnsureRegularPath(_root);
    }

    public ExternalPromptEdit? Load(EnvironmentId environmentId, ThreadId threadId)
    {
        var path = MetadataPath(environmentId, threadId);
        if (!File.Exists(path)) return null;
        EnsureRegularPath(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("External prompt recovery uses Windows protected storage.");
        if (new FileInfo(path).Length > MaximumFileBytes * 3) throw new InvalidDataException("The external prompt recovery record is too large.");
        var stored = JsonSerializer.Deserialize(WindowsProtectedStorage.Read(path), ExternalPromptJsonContext.Default.StoredExternalPromptEdit)
            ?? throw new InvalidDataException("The external prompt recovery record is invalid.");
        var edit = new ExternalPromptEdit(stored.Id, EnvironmentId.Parse(stored.EnvironmentId), ThreadId.Parse(stored.ThreadId),
            DraftId.Parse(stored.DraftId), stored.OriginalText, stored.CreatedUtc);
        if (edit.EnvironmentId != environmentId || edit.ThreadId != threadId || edit.Id == Guid.Empty)
            throw new InvalidDataException("The external prompt belongs to a different conversation.");
        return edit;
    }

    public ExternalPromptEdit Create(ThreadDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateText(draft.Text);
        if (Load(draft.EnvironmentId, draft.ThreadId) is { } existing) return existing;
        var edit = new ExternalPromptEdit(Guid.NewGuid(), draft.EnvironmentId, draft.ThreadId, draft.DraftId, draft.Text, DateTimeOffset.UtcNow);
        var path = GetFilePath(edit);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            file.Write(Encoding.UTF8.GetBytes(draft.Text));
            file.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("External prompt recovery uses Windows protected storage.");
        WindowsProtectedStorage.Write(MetadataPath(edit.EnvironmentId, edit.ThreadId),
            JsonSerializer.SerializeToUtf8Bytes(new StoredExternalPromptEdit(edit.Id, edit.EnvironmentId.Value,
                edit.ThreadId.Value, edit.DraftId.Value, edit.OriginalText, edit.CreatedUtc), ExternalPromptJsonContext.Default.StoredExternalPromptEdit));
        return edit;
    }

    public string GetFilePath(ExternalPromptEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Id == Guid.Empty) throw new ArgumentException("An editor handoff needs an identity.", nameof(edit));
        return OwnedPath($"{edit.Id:N}.md");
    }

    public async Task<string> ReadTextAsync(ExternalPromptEdit edit, CancellationToken cancellationToken = default)
    {
        var path = GetFilePath(edit);
        EnsureRegularPath(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length > MaximumFileBytes) throw new InvalidDataException("The edited prompt is too large (128K characters maximum).");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaximumTextLength + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        var text = new string(buffer, 0, count);
        ValidateText(text);
        return NormalizeText(text);
    }

    public void Discard(ExternalPromptEdit edit)
    {
        if (Load(edit.EnvironmentId, edit.ThreadId)?.Id != edit.Id)
            throw new InvalidOperationException("This editor handoff has already been replaced.");
        var file = GetFilePath(edit);
        EnsureRegularPath(file);
        // Delete only the two exact files owned by this handoff; never follow links or remove a tree.
        File.Delete(file);
        File.Delete(MetadataPath(edit.EnvironmentId, edit.ThreadId));
    }

    public ExternalPromptEdit RecordApplied(ExternalPromptEdit edit, string text)
    {
        ValidateText(text);
        if (Load(edit.EnvironmentId, edit.ThreadId)?.Id != edit.Id)
            throw new InvalidOperationException("This editor handoff has already been replaced.");
        var updated = edit with { OriginalText = text };
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("External prompt recovery uses Windows protected storage.");
        WindowsProtectedStorage.Write(MetadataPath(edit.EnvironmentId, edit.ThreadId),
            JsonSerializer.SerializeToUtf8Bytes(new StoredExternalPromptEdit(updated.Id, updated.EnvironmentId.Value,
                updated.ThreadId.Value, updated.DraftId.Value, updated.OriginalText, updated.CreatedUtc), ExternalPromptJsonContext.Default.StoredExternalPromptEdit));
        return updated;
    }

    public ExternalEditorPreferences LoadPreferences()
    {
        var path = OwnedPath("editor.json");
        if (File.Exists(path))
        {
            EnsureRegularPath(path);
            if (new FileInfo(path).Length > 32768) throw new InvalidDataException("The external editor preference is too large.");
            return JsonSerializer.Deserialize(File.ReadAllText(path), ExternalPromptJsonContext.Default.ExternalEditorPreferences)
                ?? throw new InvalidDataException("The external editor preference is invalid.");
        }
        var command = Environment.GetEnvironmentVariable("VISUAL");
        if (string.IsNullOrWhiteSpace(command)) command = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(command)) return new("notepad.exe");
        var parts = SplitArguments(command);
        return parts.Count == 0 ? new("notepad.exe") : new(parts[0], string.Join(' ', parts.Skip(1).Select(QuoteArgument)));
    }

    public Process Launch(ExternalPromptEdit edit, ExternalEditorPreferences preferences)
    {
        var start = CreateStartInfo(preferences, GetFilePath(edit));
        EnsureRegularPath(start.ArgumentList[^1]);
        if (!File.Exists(start.ArgumentList[^1])) throw new FileNotFoundException("The editor handoff file is missing.", start.ArgumentList[^1]);
        var settings = OwnedPath("editor.json");
        EnsureRegularPath(settings);
        File.WriteAllText(settings, JsonSerializer.Serialize(preferences, ExternalPromptJsonContext.Default.ExternalEditorPreferences));
        return Process.Start(start) ?? throw new InvalidOperationException("The external editor did not start.");
    }

    public static ProcessStartInfo CreateStartInfo(ExternalEditorPreferences preferences, string filePath)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferences.Executable);
        if (preferences.Executable.Length + preferences.Arguments.Length > 8192 || preferences.Executable.Contains('\0'))
            throw new ArgumentException("The editor command is invalid.", nameof(preferences));
        // Start an executable directly. Neither the prompt nor its filename is shell code.
        var start = new ProcessStartInfo(preferences.Executable.Trim().Trim('"'))
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))!,
        };
        foreach (var argument in SplitArguments(preferences.Arguments)) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(Path.GetFullPath(filePath));
        return start;
    }

    public static async Task<ExternalPromptApplyResult> ApplyAsync(ExternalPromptEdit edit, string editedText,
        ThreadDraft localDraft, Func<ThreadId, CancellationToken, Task<ThreadDraft>> load,
        Func<ThreadDraft, string, CancellationToken, Task<ThreadDraft>> save, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(localDraft);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(save);
        ValidateText(editedText);
        if (!Matches(edit, localDraft)) return new(false, localDraft, "Return to the original conversation to use this edit.");
        var current = await load(edit.ThreadId, cancellationToken).ConfigureAwait(false);
        if (!Matches(edit, current)) return new(false, current, "The original draft no longer exists. Copy the edited text to recover it.");
        if (SameText(current.Text, editedText) && (SameText(localDraft.Text, edit.OriginalText) || SameText(localDraft.Text, editedText)))
            return new(true, current);
        if (!SameText(localDraft.Text, edit.OriginalText) || !SameText(current.Text, edit.OriginalText))
            return new(false, current, "The draft changed while the editor was open. Copy the edited text and merge it into the current draft. Your editor file is retained.");
        // The freshly loaded revision makes concurrent host changes fail rather than overwrite.
        var saved = await save(current, editedText, cancellationToken).ConfigureAwait(false);
        return Matches(edit, saved) && SameText(saved.Text, editedText) ? new(true, saved)
            : new(false, saved, "The saved draft changed again. Your editor file is retained; copy its text to merge it.");
    }

    public static bool SameText(string left, string right) => NormalizeText(left) == NormalizeText(right);
    private static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool Matches(ExternalPromptEdit edit, ThreadDraft draft) => edit.EnvironmentId == draft.EnvironmentId && edit.ThreadId == draft.ThreadId && edit.DraftId == draft.DraftId;
    private static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength || text.Contains('\0')) throw new InvalidDataException("The edited prompt must contain at most 128K characters and no null characters.");
    }
    private string MetadataPath(EnvironmentId environmentId, ThreadId threadId) => OwnedPath(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environmentId.Value + ":" + threadId.Value))) + ".protected");
    private string OwnedPath(string name)
    {
        EnsureRegularPath(_root);
        var path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid external editor path.");
        return path;
    }
    private static void EnsureRegularPath(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The external editor handoff cannot use a symbolic link or junction.");
    }
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    // Windows quoting: preserve backslashes unless they escape a double quote.
    public static IReadOnlyList<string> SplitArguments(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Contains('\0')) throw new ArgumentException("Editor arguments cannot contain null characters.", nameof(command));
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var index = 0; index < command.Length; index++)
        {
            var ch = command[index];
            if (ch == '\\')
            {
                var count = 1;
                while (index + 1 < command.Length && command[index + 1] == '\\') { count++; index++; }
                if (index + 1 < command.Length && command[index + 1] == '"')
                {
                    value.Append('\\', count / 2);
                    index++;
                    if (count % 2 == 0) quoted = !quoted; else value.Append('"');
                }
                else value.Append('\\', count);
                started = true;
            }
            else if (ch == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (started) { result.Add(value.ToString()); value.Clear(); started = false; }
            }
            else { value.Append(ch); started = true; }
        }
        if (quoted) throw new ArgumentException("Close the double quote in the editor arguments.", nameof(command));
        if (started) result.Add(value.ToString());
        return result;
    }
}

[JsonSerializable(typeof(StoredExternalPromptEdit))]
[JsonSerializable(typeof(ExternalEditorPreferences))]
internal sealed partial class ExternalPromptJsonContext : JsonSerializerContext;
