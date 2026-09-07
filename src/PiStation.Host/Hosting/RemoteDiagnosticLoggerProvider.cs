using Microsoft.Extensions.Logging;

namespace PiStation.Host.Hosting;

/// <summary>Never formats request state or exception messages, which may contain credentials.</summary>
public sealed class RemoteDiagnosticLoggerProvider(Action<string> sink) : ILoggerProvider
{
    public const string HttpsCategory = "Microsoft.AspNetCore.Server.Kestrel.Https.Internal.HttpsConnectionMiddleware";

    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(categoryName, sink);
    public void Dispose() { }

    private sealed class DiagnosticLogger(string category, Action<string> write) : ILogger
    {
        private readonly bool _allowed = category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) ||
            category.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.Ordinal) ||
            category.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.Ordinal) ||
            category == "Microsoft.Extensions.Hosting.Internal.Host";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _allowed && logLevel != LogLevel.None &&
            (logLevel >= LogLevel.Warning || category == HttpsCategory && logLevel >= LogLevel.Debug);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // TLS authentication failures are Debug events in Kestrel; keep only these two.
            var tlsFailure = category == HttpsCategory && eventId.Name is "AuthenticationFailed" or "AuthenticationTimedOut";
            if (logLevel < LogLevel.Warning && !tlsFailure) return;
            var description = tlsFailure ? "TLS handshake failed or timed out" : "Transport warning/error";
            try
            {
                write($"Remote: {description}; category={category}; event={eventId.Id}; error={exception?.GetType().Name ?? "none"}");
            }
            catch
            {
                // Diagnostics must not break the listener, even when the disk is unavailable.
            }
        }
    }
}

/// <summary>A best-effort, bounded app-data log for sanitized remote diagnostics only.</summary>
public sealed class RemoteDiagnosticLog(string path)
{
    private readonly object _gate = new();
    private const long MaximumBytes = 1024 * 1024;
    private DateTimeOffset _windowStart;
    private int _windowCount;

    public void Write(string safeMessage)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _windowStart >= TimeSpan.FromMinutes(1)) { _windowStart = now; _windowCount = 0; }
            if (++_windowCount > 60) return;
            try
            {
                var fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                if (File.Exists(fullPath) && new FileInfo(fullPath).Length >= MaximumBytes)
                    File.Move(fullPath, fullPath + ".previous", overwrite: true);
                File.AppendAllText(fullPath, $"{now:O} {safeMessage}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Sharing controls remain usable when diagnostic persistence fails.
            }
        }
    }
}
