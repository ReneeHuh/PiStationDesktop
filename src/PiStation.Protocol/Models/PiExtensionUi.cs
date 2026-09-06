namespace PiStation.Protocol.Models;

public sealed record PiExtensionUiUpdate(string Id, string Method, DateTimeOffset ReceivedUtc,
    string? Key = null, string? Text = null, IReadOnlyList<string>? Lines = null, string? Placement = null, string? Severity = null);

public sealed record PiExtensionStatus(string Key, string Text);
public sealed record PiExtensionWidget(string Key, IReadOnlyList<string> Lines, string Placement);
public sealed record PiExtensionNotification(string Id, string Text, string Severity, DateTimeOffset ReceivedUtc);
public sealed record PiExtensionEditorSuggestion(string Id, string Text);

/// <summary>Ephemeral state owned by a thread runtime; snapshots preserve it across client reconnects.</summary>
public sealed record PiExtensionUiState(string? Title, IReadOnlyList<PiExtensionStatus> Statuses,
    IReadOnlyList<PiExtensionWidget> Widgets, IReadOnlyList<PiExtensionNotification> Notifications,
    PiExtensionEditorSuggestion? EditorSuggestion)
{
    public static PiExtensionUiState Empty { get; } = new(null, [], [], [], null);

    public PiExtensionUiState Apply(PiExtensionUiUpdate update)
    {
        var key = Limit(update.Key, 256);
        return update.Method switch
        {
            "notify" => this with { Notifications = Notifications.Where(item => item.Id != update.Id)
                .Append(new(update.Id, Limit(update.Text, 4096) ?? string.Empty,
                    update.Severity is "warning" or "error" ? update.Severity : "info", update.ReceivedUtc)).TakeLast(20).ToArray() },
            "setStatus" when !string.IsNullOrEmpty(key) => this with { Statuses = Statuses.Where(item => item.Key != key)
                .Concat(update.Text is null ? [] : new[] { new PiExtensionStatus(key, Limit(update.Text, 2048)!) }).TakeLast(32).ToArray() },
            "setWidget" when !string.IsNullOrEmpty(key) => this with { Widgets = Widgets.Where(item => item.Key != key)
                .Concat(update.Lines is null ? [] : new[] { new PiExtensionWidget(key,
                    update.Lines.Take(32).Select(line => Limit(line, 512) ?? string.Empty).ToArray(),
                    update.Placement == "belowEditor" ? "belowEditor" : "aboveEditor") }).TakeLast(16).ToArray() },
            "setTitle" => this with { Title = Limit(update.Text, 512) },
            "set_editor_text" => this with { EditorSuggestion = string.IsNullOrEmpty(update.Text) ? null : new(update.Id, Limit(update.Text, 64 * 1024)!) },
            _ => this,
        };
    }

    private static string? Limit(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
