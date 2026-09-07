using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PiStation.Host.Hosting;

namespace PiStation.Host.Tests;

public sealed class RemoteTransportHardeningTests
{
    [Fact]
    public void OnePeerCannotSpendOtherPeersPairingBudget()
    {
        using var limiter = RemotePairingRateLimiter.Create(2, 6);
        var noisy = Context("192.0.2.1");
        for (var i = 0; i < 2; i++) { using var allowed = limiter.AttemptAcquire(noisy); Assert.True(allowed.IsAcquired); }
        for (var i = 0; i < 10; i++) { using var denied = limiter.AttemptAcquire(noisy); Assert.False(denied.IsAcquired); }
        using var other = limiter.AttemptAcquire(Context("192.0.2.2"));
        Assert.True(other.IsAcquired);
        using var mapped = limiter.AttemptAcquire(Context("::ffff:192.0.2.1"));
        Assert.False(mapped.IsAcquired);
    }

    [Fact]
    public void GlobalLimitStillBoundsManyPeersWithoutBlockingAuthenticatedTraffic()
    {
        using var limiter = RemotePairingRateLimiter.Create(2, 3);
        for (var i = 1; i <= 3; i++) { using var allowed = limiter.AttemptAcquire(Context($"192.0.2.{i}")); Assert.True(allowed.IsAcquired); }
        using var denied = limiter.AttemptAcquire(Context("192.0.2.4"));
        Assert.False(denied.IsAcquired);
        var connection = Context("192.0.2.4");
        connection.Request.Path = "/environment";
        using var regular = limiter.AttemptAcquire(connection);
        Assert.True(regular.IsAcquired);
    }

    [Fact]
    public void DiagnosticsRecordTlsFailuresWithoutFormattingSecrets()
    {
        var messages = new List<string>();
        using var provider = new RemoteDiagnosticLoggerProvider(messages.Add);
        var logger = provider.CreateLogger(RemoteDiagnosticLoggerProvider.HttpsCategory);
        var secret = "Bearer should-never-be-logged";
        var exception = new IOException(secret);
        logger.Log(LogLevel.Debug, new EventId(1, "AuthenticationFailed"), secret, exception,
            (_, _) => throw new InvalidOperationException("Sensitive formatter must not run"));
        logger.Log(LogLevel.Debug, new EventId(3, "HttpsConnectionEstablished"), secret, null, (_, _) => secret);
        var entry = Assert.Single(messages);
        Assert.Contains("TLS handshake", entry, StringComparison.Ordinal);
        Assert.Contains("IOException", entry, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, entry, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticSinkFailureCannotBreakTransport()
    {
        using var provider = new RemoteDiagnosticLoggerProvider(_ => throw new IOException("disk full"));
        provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").Log(LogLevel.Warning, new EventId(1), "Test", null, (state, _) => state);
    }

    private static DefaultHttpContext Context(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Path = "/remote/pair/status";
        return context;
    }
}
