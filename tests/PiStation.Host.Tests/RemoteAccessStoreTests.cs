using System.Reflection;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class RemoteAccessStoreTests
{
    [Fact]
    public void ActivityTracksIndividualConnectionsAndExpiredLeasesAcrossStores()
    {
        using var directory = new HostTestDirectory();
        var clock = new TestClock();
        var path = directory.GetPath("access.db");
        using var store = new RemoteAccessStore(path, clock);
        using var cli = new RemoteAccessStore(path, clock);
        var issued = store.IssueSession();
        var authorization = store.Authenticate(issued.Token)!;
        using var first = authorization.TrackConnection!("first");
        using var second = authorization.TrackConnection!("second");
        Assert.Equal(2, Assert.Single(cli.ListDevices()).ActiveConnections);
        first.Dispose();
        Assert.Equal(1, Assert.Single(cli.ListDevices()).ActiveConnections);
        Assert.NotNull(Assert.Single(cli.ListDevices()).LastConnectedAt);
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(0, Assert.Single(cli.ListDevices()).ActiveConnections);
        cli.Revoke(issued.Device.DeviceId);
        Assert.Empty(store.ListDevices());
    }
    [Fact]
    public void InvitationIsSingleUseAndApprovalIsRequiredBeforeAuthentication()
    {
        using var directory = new HostTestDirectory();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"));
        var token = store.CreateInvitation(RemoteAccessLevel.Operate);
        var secret = RemoteAccessStore.NewSecret();
        var request = new PairingRequest(token, "Laptop", secret);
        var pending = store.BeginPairing(request);
        Assert.Null(store.Authenticate(secret));
        Assert.Throws<UnauthorizedAccessException>(() => store.BeginPairing(request));
        Assert.Throws<UnauthorizedAccessException>(() => store.GetPairingState(new(pending.RequestId, RemoteAccessStore.NewSecret())));
        Assert.Equal("pending", store.GetPairingState(new(pending.RequestId, secret)));
        store.Approve(pending.RequestId);
        Assert.Equal("approved", store.GetPairingState(new(pending.RequestId, secret)));
        var authorization = Assert.IsType<RemoteAuthorization>(store.Authenticate(secret));
        Assert.Equal(RemoteAccessLevel.Operate, authorization.Device.AccessLevel);
        store.Revoke(pending.RequestId);
        Assert.True(authorization.Revoked.IsCancellationRequested);
        Assert.Null(store.Authenticate(secret));
    }

    [Fact]
    public void DeviceApprovalAndRevocationSurviveRestartWithoutPlaintextCredentials()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        var secret = RemoteAccessStore.NewSecret();
        string id;
        using (var store = new RemoteAccessStore(path))
        {
            var request = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", secret));
            id = request.RequestId;
            store.Approve(id);
        }
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        using (var store = new RemoteAccessStore(path))
        {
            Assert.Equal(RemoteAccessLevel.ReadOnly, store.Authenticate(secret)?.Device.AccessLevel);
            store.Revoke(id);
        }
        using var restarted = new RemoteAccessStore(path);
        Assert.Null(restarted.Authenticate(secret));
        Assert.Empty(restarted.ListDevices());
    }

    [Fact]
    public void ExpiredAndRejectedPairingsCannotAuthorizeADevice()
    {
        using var directory = new HostTestDirectory();
        var clock = new TestClock();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"), clock);
        var token = store.CreateInvitation(RemoteAccessLevel.Operate);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Throws<UnauthorizedAccessException>(() => store.BeginPairing(new(token, "Expired", RemoteAccessStore.NewSecret())));
        var secret = RemoteAccessStore.NewSecret();
        var request = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Rejected", secret));
        store.Reject(request.RequestId);
        Assert.Throws<InvalidOperationException>(() => store.Approve(request.RequestId));
        Assert.Equal("rejected", store.GetPairingState(new(request.RequestId, secret)));
        Assert.Null(store.Authenticate(secret));
    }

    [Fact]
    public void VerificationCodeBindsRequestAndCredential()
    {
        using var directory = new HostTestDirectory();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"));
        var credential = RemoteAccessStore.NewSecret();
        var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", credential));
        Assert.Equal(PairingVerification.ComputeCode(pending.RequestId, credential), pending.VerificationCode);
        Assert.Matches("^[0-9]{6}$", pending.VerificationCode!);
    }

    [Fact]
    public void ApprovalAtPendingBoundaryRemainsPollableDuringGrace()
    {
        using var directory = new HostTestDirectory();
        var clock = new TestClock();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"), clock);
        var credential = RemoteAccessStore.NewSecret();
        var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", credential));
        clock.Now = pending.ExpiresAt.AddSeconds(-1);
        store.Approve(pending.RequestId);
        clock.Now = pending.ExpiresAt.AddSeconds(1);
        Assert.Equal("approved", store.GetPairingState(new(pending.RequestId, credential)));
    }

    [Fact]
    public void ExpiredRowsArePurgedWhenStoreStarts()
    {
        using var directory = new HostTestDirectory();
        var clock = new TestClock();
        var path = directory.GetPath("access.db");
        var credential = RemoteAccessStore.NewSecret();
        using (var store = new RemoteAccessStore(path, clock))
        {
            var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", credential));
            store.Approve(pending.RequestId);
        }
        clock.Now = clock.Now.AddDays(181);
        using var restarted = new RemoteAccessStore(path, clock);
        Assert.Empty(restarted.ListDevices());
        using var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RemoteDevices";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void ApprovalEnforcesDeviceCapEvenWhenRequestWasAcceptedBeforeCap()
    {
        using var directory = new HostTestDirectory();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"));
        for (var index = 0; index < 99; index++)
        {
            var credential = RemoteAccessStore.NewSecret();
            var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), $"Device {index}", credential));
            store.Approve(pending.RequestId);
            // Completed setup requests no longer need their bounded polling retention.
            store.ClearInvitations();
        }

        var first = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Late device 1", RemoteAccessStore.NewSecret()));
        var secondCredential = RemoteAccessStore.NewSecret();
        var second = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Late device 2", secondCredential));
        store.Approve(first.RequestId);
        Assert.Throws<InvalidOperationException>(() => store.Approve(second.RequestId));
        Assert.Equal("pending", store.GetPairingState(new(second.RequestId, secondCredential)));
    }

    [Fact]
    public void EveryHubMethodHasAnExplicitRemotePolicyOrLocalOnlyDecision()
    {
        var localOnly = new[] { nameof(EnvironmentHub.OpenProjectFileInEditor) };
        Assert.Equal(RemoteAccessLevel.Operate, RemoteAuthorizationFilter.MethodAccess[nameof(EnvironmentHub.DiscoverProjectPreviewServers)]);
        var methods = typeof(EnvironmentHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name);
        foreach (var method in methods)
            Assert.True(RemoteAuthorizationFilter.MethodAccess.ContainsKey(method) || localOnly.Contains(method), $"Choose a remote policy for {method}.");
    }

    [Fact]
    public void IndependentStoresSharePairingsAndImmediatelyDenyRevokedSessions()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        using var host = new RemoteAccessStore(path);
        using var cli = new RemoteAccessStore(path);
        var invitation = cli.IssueInvitation(RemoteAccessLevel.ReadOnly, label: "Laptop");
        Assert.Equal(invitation.Invitation, Assert.Single(host.ListInvitations()));
        var credential = RemoteAccessStore.NewSecret();
        var pending = host.BeginPairing(new(invitation.Token, "Viewer", credential));
        Assert.Empty(cli.ListInvitations());
        Assert.Throws<UnauthorizedAccessException>(() => cli.BeginPairing(new(invitation.Token, "Replay", RemoteAccessStore.NewSecret())));
        Assert.Equal(pending, Assert.Single(cli.ListPending()));
        var wrongCode = pending.VerificationCode == "000000" ? "000001" : "000000";
        Assert.Throws<ArgumentException>(() => cli.Approve(pending.RequestId, wrongCode));
        cli.Approve(pending.RequestId, pending.VerificationCode);
        var authorization = Assert.IsType<RemoteAuthorization>(host.Authenticate(credential));
        Assert.True(authorization.IsActive);
        Assert.Equal("approved", host.GetPairingState(new(pending.RequestId, credential)));
        Assert.True(cli.RevokeSession(pending.RequestId));
        Assert.False(authorization.IsActive);
        Assert.Null(host.Authenticate(credential));
        host.SynchronizeRevocations();
        Assert.True(authorization.Revoked.IsCancellationRequested);
        Assert.Equal("rejected", host.GetPairingState(new(pending.RequestId, credential)));
    }

    [Fact]
    public void LegacyExclusiveHostStillPreventsIncompatibleConcurrentAccess()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        using var legacy = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => new RemoteAccessStore(path));
    }

    [Fact]
    public void InvitationsAndPendingApprovalSurviveRestartWithoutPersistingTokens()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        IssuedRemotePairing invitation;
        using (var cli = new RemoteAccessStore(path)) invitation = cli.IssueInvitation(label: "Persistent link");
        var credential = RemoteAccessStore.NewSecret();
        PendingRemoteDevice pending;
        using (var host = new RemoteAccessStore(path)) pending = host.BeginPairing(new(invitation.Token, "Laptop", credential));
        using (var cli = new RemoteAccessStore(path)) cli.Approve(pending.RequestId, pending.VerificationCode);
        using (var host = new RemoteAccessStore(path))
        {
            Assert.Equal("approved", host.GetPairingState(new(pending.RequestId, credential)));
            Assert.NotNull(host.Authenticate(credential));
        }
        var contents = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain(invitation.Token, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, contents, StringComparison.Ordinal);
        Assert.DoesNotContain(invitation.Token, invitation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IssuedSessionsPreserveMetadataAndExpireAcrossStores()
    {
        using var directory = new HostTestDirectory();
        var clock = new TestClock();
        var path = directory.GetPath("access.db");
        using var cli = new RemoteAccessStore(path, clock);
        using var host = new RemoteAccessStore(path, clock);
        var issued = cli.IssueSession(RemoteAccessLevel.ReadOnly, TimeSpan.FromMinutes(15), "Automation", "build-agent");
        Assert.Equal(issued.Device, Assert.Single(host.ListDevices()));
        Assert.Equal("build-agent", issued.Device.Subject);
        Assert.DoesNotContain(issued.Token, issued.ToString(), StringComparison.Ordinal);
        var authorization = Assert.IsType<RemoteAuthorization>(host.Authenticate(issued.Token));
        Assert.True(authorization.IsActive);
        clock.Now = issued.Device.ExpiresAt;
        Assert.False(authorization.IsActive);
        Assert.Null(host.Authenticate(issued.Token));
        host.SynchronizeRevocations();
        Assert.True(authorization.Revoked.IsCancellationRequested);
        Assert.Empty(cli.ListDevices());
    }

    [Fact]
    public void InvitationRevocationAndSessionLimitsAreShared()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        using var first = new RemoteAccessStore(path);
        using var second = new RemoteAccessStore(path);
        var invitation = first.IssueInvitation();
        Assert.True(second.RevokeInvitation(invitation.Invitation.Id));
        Assert.Throws<UnauthorizedAccessException>(() => first.BeginPairing(new(invitation.Token, "Denied", RemoteAccessStore.NewSecret())));
        for (var i = 0; i < 100; i++) (i % 2 == 0 ? first : second).IssueSession();
        Assert.Throws<InvalidOperationException>(() => first.IssueSession());
        Assert.Throws<InvalidOperationException>(() => second.IssueSession());
        Assert.True(second.RevokeSession(first.ListDevices()[0].DeviceId));
        Assert.NotNull(first.IssueSession());
    }

    [Fact]
    public void InvitationRedemptionIsAtomicBetweenIndependentConnections()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        using var first = new RemoteAccessStore(path);
        using var second = new RemoteAccessStore(path);
        var invitation = first.IssueInvitation();
        var accepted = 0;
        Parallel.ForEach(new[] { first, second }, store =>
        {
            try
            {
                store.BeginPairing(new(invitation.Token, "Concurrent", RemoteAccessStore.NewSecret()));
                Interlocked.Increment(ref accepted);
            }
            catch (UnauthorizedAccessException) { }
        });
        Assert.Equal(1, accepted);
        Assert.Single(first.ListPending());
    }

    [Fact]
    public void InvalidLifetimesAndLabelsDoNotCreateGrants()
    {
        using var directory = new HostTestDirectory();
        using var store = new RemoteAccessStore(directory.GetPath("access.db"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.IssueInvitation(ttl: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.IssueSession(ttl: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.IssueSession(ttl: TimeSpan.FromDays(181)));
        Assert.Throws<ArgumentException>(() => store.IssueSession(label: "newline\n"));
        Assert.Throws<ArgumentException>(() => store.IssueSession(subject: new string('x', 81)));
        Assert.Empty(store.ListInvitations());
        Assert.Empty(store.ListDevices());
    }

    [Fact]
    public void ExistingDeviceSchemaIsMigratedWithoutInvalidatingCredentials()
    {
        using var directory = new HostTestDirectory();
        var path = directory.GetPath("access.db");
        var token = RemoteAccessStore.NewSecret();
        using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                CREATE TABLE RemoteDevices (DeviceId TEXT PRIMARY KEY, DeviceName TEXT NOT NULL,
                    CredentialHash TEXT NOT NULL UNIQUE, AccessLevel INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                INSERT INTO RemoteDevices VALUES ($id,'Legacy viewer',$hash,0,$expiry);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))));
            command.Parameters.AddWithValue("$expiry", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        using var store = new RemoteAccessStore(path);
        var authorization = Assert.IsType<RemoteAuthorization>(store.Authenticate(token));
        Assert.Equal(RemoteAccessLevel.ReadOnly, authorization.Device.AccessLevel);
        Assert.Null(authorization.Device.Subject);
        Assert.NotNull(store.IssueSession(subject: "new metadata"));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
