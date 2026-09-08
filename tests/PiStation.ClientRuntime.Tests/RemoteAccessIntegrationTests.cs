using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.Threads;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteAccessIntegrationTests
{
    [Fact]
    public async Task InterruptedTlsHandshakeRetriesWithoutClaimingCertificateMismatch()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        var issued = access.IssueSession(RemoteAccessLevel.ReadOnly);
        using var certificate = CreateCertificate();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var interrupted = new CancellationTokenSource();
        var accept = Task.Run(async () =>
        {
            try { while (!interrupted.IsCancellationRequested) { using var socket = await listener.AcceptTcpClientAsync(interrupted.Token); } }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { }
        }, timeout.Token);
        await using var client = new EnvironmentClient(new()
        {
            HubAddress = new Uri($"https://127.0.0.1:{port}/environment"), BearerCredential = issued.Token,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            ReconnectDelays = [TimeSpan.FromMilliseconds(20)], RetryJitter = 0,
        });
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(timeout.Token));
            while (client.Diagnostics.Attempt < 3 && client.ConnectionState != EnvironmentConnectionState.TrustRequired) await Task.Delay(20, timeout.Token);
            Assert.Equal(EnvironmentConnectionState.Retrying, client.ConnectionState);
            Assert.NotEqual(ConnectionFailure.Certificate, client.Diagnostics.Failure);
        }
        finally { await interrupted.CancelAsync(); await accept; listener.Stop(); }
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, state) => { if (state.State == EnvironmentConnectionState.Connected) connected.TrySetResult(); };
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, port, certificate, cancellationToken: timeout.Token);
        await connected.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task CatalogReplaysAcrossDevicesAndReadOnlyStreamsHaveConsumerLifetimes()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var options = directory.CreateHostOptions();
        var factory = new CountingProcessFactory(new PiProcessFactory(options));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(options, factory, timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        var credential = RemoteAccessStore.NewSecret();
        access.Approve(access.BeginPairing(new(access.CreateInvitation(RemoteAccessLevel.ReadOnly), "Catalog observer", credential)).RequestId);
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        await using var viewer = new EnvironmentClient(new()
        {
            HubAddress = new(remote.Address, "/environment"), BearerCredential = credential,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        });
        await using var writer = new EnvironmentClient(new() { HubAddress = local.HubAddress, BearerCredential = local.BearerCredential });
        await viewer.ConnectAsync(timeout.Token);
        await writer.ConnectAsync(timeout.Token);
        Assert.True(viewer.Catalog.IsSynchronized);
        Assert.True(writer.Catalog.IsSynchronized);
        var project = await writer.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var threads = new List<ThreadDescriptor>();
        for (var i = 0; i < 100; i++) threads.Add(await writer.CreateThreadAsync(new(project.ProjectId, $"Thread {i}"), timeout.Token));
        await WaitForCatalogAsync(viewer.Catalog, () => viewer.Catalog.Threads.Count == 100, timeout.Token);
        Assert.Equal(project.ProjectId, Assert.Single(viewer.Catalog.Projects).ProjectId);
        foreach (var thread in threads)
        {
            await using var first = viewer.SubscribeThread(thread.ThreadId);
            await using var second = viewer.SubscribeThread(thread.ThreadId);
            Assert.Same(first.Store, second.Store);
            Assert.Equal(1, viewer.ActiveThreadStreamCount);
            await WaitForProjectionAsync(first.Store, p => p.RuntimeState == ThreadRuntimeState.Stopped, timeout.Token);
            await first.DisposeAsync();
            Assert.Equal(1, viewer.ActiveThreadStreamCount);
            await second.DisposeAsync();
            Assert.Equal(0, viewer.ActiveThreadStreamCount);
            while (local.Environment.ActiveThreadStreamCount != 0) await Task.Delay(10, timeout.Token);
        }
        await viewer.DisconnectAsync(timeout.Token);
        var missed = await writer.CreateThreadAsync(new(project.ProjectId, "While offline"), timeout.Token);
        await viewer.ConnectAsync(timeout.Token);
        Assert.True(viewer.Catalog.IsSynchronized);
        Assert.Contains(viewer.Catalog.Threads, t => t.ThreadId == missed.ThreadId);
        Assert.Equal(0, factory.StartCount);
        Assert.Equal(101, viewer.ThreadMetadata.GetProjectThreads(project.ProjectId).Count);
    }

    private static async Task WaitForCatalogAsync(CatalogStore store, Func<bool> predicate, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) { if (predicate()) completion.TrySetResult(); }
        store.Changed += Changed;
        try { if (predicate()) return; await completion.Task.WaitAsync(token); }
        finally { store.Changed -= Changed; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingStreamSurvivesProlongedOutageAndManualRecovery(bool manualRecovery)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        var credential = RemoteAccessStore.NewSecret();
        access.Approve(access.BeginPairing(new(access.CreateInvitation(RemoteAccessLevel.Operate), "Recovery", credential)).RequestId);
        var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        try
        {
            var endpoint = remote.Address;
            await using var client = new EnvironmentClient(new()
            {
                HubAddress = new(endpoint, "/environment"), BearerCredential = credential,
                CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
                ReconnectDelays = [TimeSpan.FromMilliseconds(20)], RetryJitter = 0,
            });
            await client.ConnectAsync(timeout.Token);
            var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
            var thread = await client.CreateThreadAsync(new(project.ProjectId, "Existing stream"), timeout.Token);
            await using var subscription = client.SubscribeThread(thread.ThreadId);
            await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready, timeout.Token);
            var exhaustedOldPolicy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionStateChanged += (_, args) =>
            {
                if (args.Diagnostics?.Attempt >= 7) exhaustedOldPolicy.TrySetResult();
                if (exhaustedOldPolicy.Task.IsCompleted && args.State == EnvironmentConnectionState.Connected) recovered.TrySetResult();
                if (args.State == EnvironmentConnectionState.AuthenticationRequired) rejected.TrySetResult();
            };
            await remote.DisposeAsync();
            remote = null!;
            await exhaustedOldPolicy.Task.WaitAsync(timeout.Token);
            remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, endpoint.Port, certificate, cancellationToken: timeout.Token);
            if (manualRecovery) await client.ConnectAsync(timeout.Token);
            await recovered.Task.WaitAsync(timeout.Token);
            await client.StartTurnAsync(thread.ThreadId, "After recovery", subscription.Store.Current!.ProjectionEpoch, cancellationToken: timeout.Token);
            var projection = await WaitForProjectionAsync(subscription.Store, p => p.Messages.Count == 2 && p.RuntimeState == ThreadRuntimeState.Ready, timeout.Token);
            Assert.Equal("After recovery", projection.Messages[0].Text);
            access.Revoke(Assert.Single(access.ListDevices()).DeviceId);
            await rejected.Task.WaitAsync(timeout.Token);
            Assert.Equal(ConnectionFailure.Authentication, client.Diagnostics.Failure);
        }
        finally { if (remote is not null) await remote.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SupportedFileSizesSaveWithoutDisconnecting(int transport)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(), cancellationToken: timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        var credential = RemoteAccessStore.NewSecret();
        access.Approve(access.BeginPairing(new(access.CreateInvitation(RemoteAccessLevel.Operate), "Editor", credential)).RequestId);
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        var clientOptions = transport == 1 ? new ClientRuntimeOptions
        {
            HubAddress = new(remote.Address, "/environment"), BearerCredential = credential,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        } : new ClientRuntimeOptions { HubAddress = local.HubAddress, BearerCredential = local.BearerCredential };
        if (transport == 2)
        {
            var info = await SshEnvironmentHost.TryDiscoverAsync(Path.Combine(directory.Path, "data"), timeout.Token);
            Assert.NotNull(info);
            clientOptions = new() { HubAddress = new Uri($"https://127.0.0.1:{info.Port}/environment"),
                BearerCredential = info.BearerCredential, CertificateFingerprint = info.CertificateFingerprint };
        }
        await using var client = new EnvironmentClient(clientOptions);
        await client.ConnectAsync(timeout.Token);
        var root = directory.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(root, "edit.txt"), "original", timeout.Token);
        var project = await client.AddProjectAsync(new(root), timeout.Token);
        var opened = await client.ReadProjectFileAsync(new(project.ProjectId, "edit.txt"), timeout.Token);
        var revision = opened.Revision;
        foreach (var content in new[] { new string('a', 40_000), new string('a', FileReadDefaults.MaximumWriteBytes),
                     new string('\u0001', FileReadDefaults.MaximumWriteBytes), new string('é', FileReadDefaults.MaximumWriteBytes / 2) })
        {
            var saved = await client.SaveProjectFileAsync(new(project.ProjectId, "edit.txt", content, revision), timeout.Token);
            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(root, "edit.txt"), timeout.Token));
            revision = saved.Revision;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => client.SaveProjectFileAsync(new(project.ProjectId, "edit.txt", new string('a', FileReadDefaults.MaximumWriteBytes + 1), revision), timeout.Token));
        await Assert.ThrowsAnyAsync<Exception>(() => client.SaveProjectFileAsync(new(project.ProjectId, "edit.txt", "stale", opened.Revision), timeout.Token));
        Assert.Single(await client.ListProjectsAsync(timeout.Token));
        Assert.Equal(EnvironmentConnectionState.Connected, client.ConnectionState);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("crash-once")]
    public async Task ReadOnlyRpcNeverLaunchesOrRestartsPiButObservesLocalActivity(string scenario)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var options = directory.CreateHostOptions(scenario);
        var factory = new CountingProcessFactory(new PiProcessFactory(options));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(options, factory, timeout.Token);
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token);
        var secret = RemoteAccessStore.NewSecret();
        access.Approve(access.BeginPairing(new(access.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", secret)).RequestId);
        await using var viewer = new EnvironmentClient(new()
        {
            HubAddress = new(remote.Address, "/environment"), BearerCredential = secret,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        });
        await using var operatorClient = new EnvironmentClient(new() { HubAddress = local.HubAddress, BearerCredential = local.BearerCredential });
        await viewer.ConnectAsync(timeout.Token);
        await operatorClient.ConnectAsync(timeout.Token);
        var project = await operatorClient.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        var thread = await operatorClient.CreateThreadAsync(new(project.ProjectId, "Passive viewer"), timeout.Token);
        var configuration = await viewer.GetThreadPiConfigurationAsync(thread.ThreadId, timeout.Token);
        Assert.Empty(configuration.Capabilities.Models);
        Assert.Empty((await viewer.GetThreadDraftAsync(thread.ThreadId, timeout.Token)).Text);
        await using var subscription = viewer.SubscribeThread(thread.ThreadId);
        await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Stopped, timeout.Token);
        Assert.Equal(0, factory.StartCount);

        await using var localSubscription = operatorClient.SubscribeThread(thread.ThreadId);
        var ready = await WaitForProjectionAsync(localSubscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready, timeout.Token);
        Assert.Equal(1, factory.StartCount);
        await operatorClient.StartTurnAsync(thread.ThreadId, "observe local activity", ready.ProjectionEpoch, cancellationToken: timeout.Token);
        if (scenario == "normal")
        {
            var observed = await WaitForProjectionAsync(subscription.Store,
                p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 2, timeout.Token);
            Assert.Equal("observe local activity", observed.Messages[0].Text);
        }
        else
        {
            await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Crashed, timeout.Token);
            await viewer.GetThreadPiConfigurationAsync(thread.ThreadId, timeout.Token);
            await viewer.DisconnectAsync(timeout.Token);
            await viewer.ConnectAsync(timeout.Token);
            await WaitForProjectionAsync(subscription.Store, p => p.RuntimeState == ThreadRuntimeState.Crashed, timeout.Token);
        }
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public async Task PairedRemoteClientRunsAHostTurnReconnectsAndIsRevokedWhileSubscribed()
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        var invitation = new RemoteInvitation(remote.Address, certificate.GetCertHashString(HashAlgorithmName.SHA256), store.CreateInvitation(RemoteAccessLevel.Operate));
        var saved = await RemotePairingClient.PairAsync(invitation, "Remote laptop", new CallbackProgress(_ =>
        {
            var pending = Assert.Single(store.ListPending());
            Assert.Null(store.Authenticate(local.BearerCredential));
            store.Approve(pending.RequestId);
        }), timeout.Token);
        await using var client = new EnvironmentClient(saved.CreateOptions());
        await using var localClient = new EnvironmentClient(new() { HubAddress = local.HubAddress, BearerCredential = local.BearerCredential });
        await client.ConnectAsync(timeout.Token);
        await localClient.ConnectAsync(timeout.Token);
        Assert.Contains("remote.access", client.Descriptor!.Capabilities);
        Assert.Contains("preview.discover", client.Descriptor.Capabilities);
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("project")), timeout.Token);
        Assert.Equal(project.ProjectId, Assert.Single(await localClient.ListProjectsAsync(timeout.Token)).ProjectId);
        var thread = await client.CreateThreadAsync(new(project.ProjectId, "Remote turn"), timeout.Token);
        await using var remoteSubscription = client.SubscribeThread(thread.ThreadId);
        await using var localSubscription = localClient.SubscribeThread(thread.ThreadId);
        var ready = await WaitForProjectionAsync(remoteSubscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready, timeout.Token);
        await client.StartTurnAsync(thread.ThreadId, "Hello over HTTPS", ready.ProjectionEpoch, cancellationToken: timeout.Token);
        await WaitForProjectionAsync(remoteSubscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 2, timeout.Token);
        var localProjection = await WaitForProjectionAsync(localSubscription.Store, p => p.RuntimeState == ThreadRuntimeState.Ready && p.Messages.Count == 2, timeout.Token);
        Assert.Equal("Hello over HTTPS", localProjection.Messages[0].Text);
        await client.DisconnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        Assert.Equal(EnvironmentConnectionState.Connected, client.ConnectionState);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += (_, args) =>
        {
            if (args.State is EnvironmentConnectionState.Retrying or EnvironmentConnectionState.Disconnected or EnvironmentConnectionState.AuthenticationRequired)
                disconnected.TrySetResult();
        };
        using (var cli = new RemoteAccessStore(Path.Combine(directory.Path, "access", "access.db")))
            cli.Revoke(Assert.Single(cli.ListDevices()).DeviceId);
        await disconnected.Task.WaitAsync(timeout.Token);
        await using var rejected = new EnvironmentClient(saved.CreateOptions());
        await Assert.ThrowsAnyAsync<Exception>(() => rejected.ConnectAsync(timeout.Token));
        Assert.Single(await localClient.ListProjectsAsync(timeout.Token));
    }

    [Fact]
    public async Task RemoteListenerRejectsLocalBootstrapWrongPinsReadOnlyWritesAndUpload()
    {
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        await using var bootstrap = new EnvironmentClient(new()
        { HubAddress = new(remote.Address, "/environment"), CertificateFingerprint = fingerprint, BearerCredential = local.BearerCredential });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => bootstrap.ConnectAsync());
        var secret = RemoteAccessStore.NewSecret();
        var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.ReadOnly), "Viewer", secret));
        store.Approve(pending.RequestId);
        var options = new ClientRuntimeOptions
        { HubAddress = new(remote.Address, "/environment"), CertificateFingerprint = fingerprint, BearerCredential = secret };
        await using var wrongPin = new EnvironmentClient(options with { CertificateFingerprint = new string('0', 64) });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => wrongPin.ConnectAsync());
        await using var viewer = new EnvironmentClient(options);
        await viewer.ConnectAsync();
        Assert.Empty(await viewer.ListProjectsAsync());
        Assert.DoesNotContain("thread.operate", viewer.Descriptor!.Capabilities);
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.AddProjectAsync(new(directory.CreateDirectory("forbidden"))));
        Assert.Empty(await local.Environment.ListProjectsAsync());
        using var http = new HttpClient(RemoteTransport.CreateHandler(fingerprint)) { BaseAddress = remote.Address };
        http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        using var upload = await http.PostAsync("threads/fake/draft-attachments", new StringContent("test"));
        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        await using var wrongEnvironment = new EnvironmentClient(options with { ExpectedEnvironmentId = EnvironmentId.New() });
        var identityError = await Assert.ThrowsAsync<ConnectionValidationException>(() => wrongEnvironment.ConnectAsync());
        Assert.Equal(ConnectionFailure.Identity, identityError.Failure);
        Assert.Equal(EnvironmentConnectionState.TrustRequired, wrongEnvironment.ConnectionState);
    }

    [Fact]
    public async Task StoppingRemoteListenerLeavesLocalHostUsableAndCredentialsWorkAfterRestart()
    {
        using var directory = new ClientTestDirectory();
        var options = directory.CreateHostOptions();
        var path = Path.Combine(directory.CreateDirectory("access"), "access.db");
        using var certificate = CreateCertificate();
        var secret = RemoteAccessStore.NewSecret();
        EnvironmentId environmentId;
        await using (var local = await EmbeddedEnvironmentHost.StartAsync(options))
        using (var store = new RemoteAccessStore(path))
        {
            var pending = store.BeginPairing(new(store.CreateInvitation(RemoteAccessLevel.Operate), "Persistent", secret));
            store.Approve(pending.RequestId);
            var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
            await using var client = new EnvironmentClient(new()
            { HubAddress = new(remote.Address, "/environment"), BearerCredential = secret, CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256) });
            await client.ConnectAsync();
            environmentId = client.Descriptor!.EnvironmentId;
            await remote.DisposeAsync();
            var project = await local.Environment.AddProjectAsync(new(directory.CreateDirectory("survived")));
            Assert.Equal(environmentId, project.EnvironmentId);
        }
        await using var restartedLocal = await EmbeddedEnvironmentHost.StartAsync(options);
        using var restartedStore = new RemoteAccessStore(path);
        await using var restartedRemote = await RemoteEnvironmentHost.StartAsync(restartedLocal.Environment, restartedStore, IPAddress.Loopback, 0, certificate);
        await using var reconnected = new EnvironmentClient(new()
        {
            HubAddress = new(restartedRemote.Address, "/environment"), BearerCredential = secret,
            CertificateFingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256), ExpectedEnvironmentId = environmentId,
        });
        await reconnected.ConnectAsync();
        Assert.Equal("survived", Assert.Single(await reconnected.ListProjectsAsync()).DisplayName);
    }

    [Fact]
    public async Task InvitationCannotBeReplayedOverHttp()
    {
        using var directory = new ClientTestDirectory();
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        using var store = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var certificate = CreateCertificate();
        await using var remote = await RemoteEnvironmentHost.StartAsync(local.Environment, store, IPAddress.Loopback, 0, certificate);
        using var http = new HttpClient(RemoteTransport.CreateHandler(certificate.GetCertHashString(HashAlgorithmName.SHA256))) { BaseAddress = remote.Address };
        var request = new PairingRequest(store.CreateInvitation(RemoteAccessLevel.Operate), "Laptop", RemoteAccessStore.NewSecret());
        using var first = await http.PostAsJsonAsync("remote/pair", request, ProtocolJsonContext.Default.PairingRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await http.PostAsJsonAsync("remote/pair", request, ProtocolJsonContext.Default.PairingRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    private static async Task<ThreadProjection> WaitForProjectionAsync(ProjectionStore store, Func<ThreadProjection, bool> predicate, CancellationToken token)
    {
        var completion = new TaskCompletionSource<ThreadProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, ProjectionChangedEventArgs args)
        { if (args.Projection is { } projection && predicate(projection)) completion.TrySetResult(projection); }
        store.Changed += Changed;
        try
        {
            if (store.Current is { } current && predicate(current)) return current;
            return await completion.Task.WaitAsync(token);
        }
        finally { store.Changed -= Changed; }
    }

    private sealed class CallbackProgress(Action<string> callback) : IProgress<string>
    { public void Report(string value) => callback(value); }

    private sealed class CountingProcessFactory(IPiProcessFactory inner) : IPiProcessFactory
    {
        private int _starts;
        public int StartCount => Volatile.Read(ref _starts);
        public Task<PiStation.PiRpc.Process.PiProcess> StartAsync(ProjectDescriptor project,
            PiStation.Host.Persistence.HostThreadRecord thread, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return inner.StartAsync(project, thread, cancellationToken);
        }
    }
}
