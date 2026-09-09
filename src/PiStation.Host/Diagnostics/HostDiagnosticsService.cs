using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiStation.Host.Persistence;
using PiStation.Host.SourceControl;
using PiStation.Host.Terminals;
using PiStation.Host.Threads;
using PiStation.Protocol;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Diagnostics;

internal sealed partial class HostDiagnosticsService(
    HostOptions options,
    HostDatabase database,
    PiThreadRegistry threads,
    TerminalSessionRegistry terminals)
{
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly HostDatabase _database = database;
    private readonly HostOptions _options = options;
    private readonly TerminalSessionRegistry _terminals = terminals;
    private readonly PiThreadRegistry _threads = threads;

    public void Record(string message)
    {
        _logs.Enqueue($"{DateTimeOffset.UtcNow:O} {Redact(message)}");
        while (_logs.Count > 500)
        {
            _logs.TryDequeue(out _);
        }
    }

    public async Task<DiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<RuntimeDiagnostic>
        {
            new("Pi runtime", _options.PiInstallation is null ? "Unavailable" : "Available",
                _options.PiInstallation is null ? "Pi was not discovered." : $"Pi {_options.PiInstallation.PiVersion}"),
            new("Host database", File.Exists(_options.DatabasePath) ? "Available" : "Unavailable",
                File.Exists(_options.DatabasePath) ? "SQLite persistence is online." : "The host database has not been created."),
            new("Application data", Directory.Exists(_options.CanonicalDataRoot) ? "Available" : "Unavailable",
                _options.CanonicalDataRoot, IsSensitive: true),
        };
        diagnostics.AddRange(await SourceControlHostingService.GetToolDiagnosticsAsync(cancellationToken).ConfigureAwait(false));
        var process = Process.GetCurrentProcess();
        var resources = new ResourceTelemetry(
            DateTimeOffset.UtcNow,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.TotalProcessorTime.TotalSeconds,
            process.Threads.Count,
            File.Exists(_options.DatabasePath) ? new FileInfo(_options.DatabasePath).Length : 0,
            _threads.ActiveCount,
            _terminals.ActiveCount);
        var now = DateTimeOffset.UtcNow;
        var usage = await _database.GetUsageSummaryAsync(now.AddDays(-30), now, cancellationToken).ConfigureAwait(false);
        return new DiagnosticsSnapshot(
            diagnostics,
            resources,
            usage,
            _logs.Reverse().Take(200).Reverse().ToArray(),
            typeof(HostDiagnosticsService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ProtocolVersion.Current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Check for updates in the desktop app. Directly installed developer packages have no automatic update feed.");
    }

    public async Task<ExportDiagnosticsResult> ExportAsync(
        ExportDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        var destination = Path.GetFullPath(request.DestinationPath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The diagnostic export path requires a directory.", nameof(request));
        Directory.CreateDirectory(parent);
        var download = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(download.Content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ExportDiagnosticsResult(destination, stream.Length, DateTimeOffset.UtcNow);
    }

    public async Task<DiagnosticsDownload> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var redacted = snapshot with
        {
            Diagnostics = snapshot.Diagnostics.Select(item => item.IsSensitive
                ? item with { Detail = "[redacted]" }
                : item with { Detail = Redact(item.Detail) }).ToArray(),
            RecentLogs = snapshot.RecentLogs.Select(Redact).ToArray(),
        };
        return new(JsonSerializer.SerializeToUtf8Bytes(redacted, ProtocolJsonContext.Default.DiagnosticsSnapshot), DateTimeOffset.UtcNow);
    }

    private static string Redact(string value)
    {
        var result = SecretPattern().Replace(value, "$1=[redacted]");
        result = BearerPattern().Replace(result, "$1 [redacted]");
        return HomePathPattern().Replace(result, "%USERPROFILE%$1");
    }

    [GeneratedRegex("(?i)\\b(token|secret|password|api[_-]?key)\\s*[=:]\\s*[^\\s;,]+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?i)\\b(authorization|bearer)\\s+[^\\s;,]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)[A-Z]:\\\\Users\\\\[^\\\\/\\s]+([\\\\/])")]
    private static partial Regex HomePathPattern();
}
