using System.Diagnostics;
using System.Globalization;

namespace PiStation.ClientRuntime.Tests;

public sealed class TailscaleServeProcessTests
{
    [Fact]
    public async Task AlreadyCanceledQueryDoesNotLaunchAProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailscaleServeProcess.QueryAsync(
            @"C:\does-not-exist\tailscale.exe", [], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TailscaleServeProcess.QueryAsync([], cancellation.Token));
    }

    [Fact]
    public async Task FastStatusProcessCanExitBeforeItsOutputIsRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 10; index++)
            Assert.Equal("{}", await TailscaleServeProcess.QueryAsync(FixtureExecutable(), Arguments("query", root.Path), deadline.Token));
    }

    [Fact]
    public async Task NonzeroExitDoesNotExposeCliOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TailscaleServeProcess.QueryAsync(
            FixtureExecutable(), Arguments("error", root.Path), deadline.Token));
        Assert.DoesNotContain("fixture-private-detail", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Tailscale", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelingAStuckQueryClosesItsProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = new CancellationTokenSource();
        var query = TailscaleServeProcess.QueryAsync(FixtureExecutable(), Arguments("wait", root.Path), cancellation.Token);
        try
        {
            using var process = await ReadProcessAsync(root.Path, "wait", deadline.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(deadline.Token));
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            await cancellation.CancelAsync();
            try { await query; }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData("stdout-limit")]
    [InlineData("stderr-limit")]
    public async Task ExcessiveOutputStopsTheOwnedProcess(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => TailscaleServeProcess.QueryAsync(
            FixtureExecutable(), Arguments(mode, root.Path), deadline.Token));
        Assert.Contains("size limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingServeClosesDescendantsAndLeavesAnUnrelatedProcessRunning()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var unrelated = Start("wait", root.Path);
        await using var owned = new TailscaleServeProcess(FixtureExecutable(), Arguments("tree", root.Path));
        using var tree = await ReadProcessAsync(root.Path, "tree", deadline.Token);
        using var leaf = await ReadProcessAsync(root.Path, "leaf", deadline.Token);
        await owned.DisposeAsync();
        await Task.WhenAll(tree.WaitForExitAsync(deadline.Token), leaf.WaitForExitAsync(deadline.Token));
        Assert.True(owned.HasExited);
        Assert.False(unrelated.Process.HasExited);
        await owned.DisposeAsync(); // Repeated stop is safe.
    }

    [Fact]
    public async Task AbruptOwnerExitClosesServeAndItsDescendants()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new ClientTestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var owner = Start("owner", root.Path);
        await using var unrelated = Start("wait", root.Path);
        using var ready = await ReadProcessAsync(root.Path, "owner", deadline.Token);
        using var tree = await ReadProcessAsync(root.Path, "tree", deadline.Token);
        using var leaf = await ReadProcessAsync(root.Path, "leaf", deadline.Token);
        owner.Process.Kill(entireProcessTree: false); // Only the job handle can terminate the descendants.
        await Task.WhenAll(owner.Process.WaitForExitAsync(deadline.Token), tree.WaitForExitAsync(deadline.Token), leaf.WaitForExitAsync(deadline.Token));
        Assert.False(unrelated.Process.HasExited);
    }

    private static async Task<Process> ReadProcessAsync(string root, string mode, CancellationToken token)
    {
        var path = Path.Combine(root, mode + ".pid");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path, token), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                var process = Process.GetProcessById(id);
                _ = process.SafeHandle; // Capture this process before the test terminates its owner.
                return process;
            }
            await Task.Delay(20, token);
        }
    }

    private static string[] Arguments(string mode, string root) => ["--serve-process-fixture", mode, root];

    private static OwnedFixtureProcess Start(string mode, string root)
    {
        var start = new ProcessStartInfo(FixtureExecutable()) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in Arguments(mode, root)) start.ArgumentList.Add(argument);
        return new(Process.Start(start)!);
    }

    private sealed class OwnedFixtureProcess(Process process) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { Process.Dispose(); }
        }
    }

    private static string FixtureExecutable()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "PiStationDesktop.slnx"))) continue;
            var executable = Path.Combine(directory.FullName, "tests", "PiStation.RemoteUiFixture", "bin", configuration, "net10.0-windows", "PiStation.RemoteUiFixture.exe");
            return File.Exists(executable) ? executable : throw new FileNotFoundException("Build the remote acceptance fixture first.", executable);
        }
        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }
}
