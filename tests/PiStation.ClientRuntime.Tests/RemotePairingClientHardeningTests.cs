using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemotePairingClientHardeningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapacityAndRateLimitFailuresHaveActionableMessages(bool throttle)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        if (throttle)
        {
            using var http = new HttpClient(RemoteTransport.CreateHandler(Fingerprint(certificate))) { BaseAddress = remote.Address };
            var invalid = new PairingRequest(RemoteAccessStore.NewSecret(), "Invalid", RemoteAccessStore.NewSecret());
            for (var i = 0; i < 120; i++)
            {
                using var response = await http.PostAsJsonAsync("remote/pair", invalid, ProtocolJsonContext.Default.PairingRequest, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
        else
        {
            for (var i = 0; i < 32; i++)
                store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), $"Pending {i}", RemoteAccessStore.NewSecret()));
        }
        var invitation = new RemoteInvitation(remote.Address, Fingerprint(certificate), store.CreateInvitation(RemoteAccessLevel.ReadOnly));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemotePairingClient.PairAsync(invitation, "Laptop", cancellationToken: timeout.Token));
        Assert.Contains(throttle ? "Too many pairing attempts" : "pairing/device limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidInvitationProducesFriendlyMessage()
    {
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        var invitation = new RemoteInvitation(remote.Address, Fingerprint(certificate), RemoteAccessStore.NewSecret());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RemotePairingClient.PairAsync(invitation, "Laptop"));
        Assert.Contains("invalid, expired, or already used", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectedAndExpiredPollsProduceFriendlyMessages()
    {
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        var clock = new TestClock();
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"), clock);
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        var invitation = new RemoteInvitation(remote.Address, Fingerprint(certificate), store.CreateInvitation(RemoteAccessLevel.ReadOnly));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => RemotePairingClient.PairAsync(invitation, "Rejected", new CallbackProgress(value =>
        {
            var pending = Assert.Single(store.ListPending());
            store.Reject(pending.RequestId);
        })));
        Assert.Contains("declined or expired", rejected.Message, StringComparison.OrdinalIgnoreCase);

        var expiringClock = new TestClock();
        using var expiringStore = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("expired"), "access.db"), expiringClock);
        await using var expiringRemote = await RemoteEnvironmentHost.StartAsync(local.Environment, expiringStore, IPAddress.Loopback, 0, certificate);
        var expiringInvitation = new RemoteInvitation(expiringRemote.Address, Fingerprint(certificate), expiringStore.CreateInvitation(RemoteAccessLevel.ReadOnly));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() => RemotePairingClient.PairAsync(expiringInvitation, "Expired", new CallbackProgress(_ =>
        {
            var pending = Assert.Single(expiringStore.ListPending());
            expiringClock.Now = pending.ExpiresAt.AddSeconds(1);
        })));
        Assert.Contains("expired or was rejected", expired.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationProgressMatchesHostCodeAndApprovalCompletes()
    {
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        var clock = new TestClock();
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"), clock);
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        var invitation = new RemoteInvitation(remote.Address, Fingerprint(certificate), store.CreateInvitation(RemoteAccessLevel.ReadOnly));
        string? progressText = null;
        var saved = await RemotePairingClient.PairAsync(invitation, "Laptop", new CallbackProgress(value =>
        {
            progressText = value;
            var pending = Assert.Single(store.ListPending());
            Assert.Contains($"Verification code: {pending.VerificationCode}", value, StringComparison.Ordinal);
            clock.Now = pending.ExpiresAt.AddSeconds(-1);
            store.Approve(pending.RequestId);
            clock.Now = pending.ExpiresAt.AddSeconds(1);
        }));
        Assert.NotNull(progressText);
        Assert.Single(store.ListDevices());
        Assert.Equal(RemoteAccessLevel.ReadOnly, store.Authenticate(saved.DeviceCredential)?.Device.AccessLevel);
    }

    private static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    { public void Report(string value) => callback(value); }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
