using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiStation.ClientRuntime.Ssh;

internal sealed record SshHostBundle(string Path, string Hash, long Size)
{
    public static async Task<SshHostBundle> LoadAsync(CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "SshHost", "host-win-x64.zip");
        if (!File.Exists(path))
            throw new InvalidOperationException("The bundled Windows host is missing. Repair the desktop installation or supply an existing remote PiStation.Server.exe path.");
        await using var stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > 256 * 1024 * 1024)
            throw new InvalidOperationException("The bundled Windows host archive has an invalid size.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return new(path, hash, stream.Length);
    }

    public string Bootstrap(SshConnectionProfile profile)
    {
        var script = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "SshHost", "install-host.ps1"));
        var args = new List<string> { SshCommands.Quote("attach") };
        if (!string.IsNullOrWhiteSpace(profile.DataRoot)) { args.Add(SshCommands.Quote("--data-root")); args.Add(SshCommands.DataRootArgument(profile.DataRoot)); }
        if (!string.IsNullOrWhiteSpace(profile.PiExecutable)) { args.Add(SshCommands.Quote("--pi-executable")); args.Add(SshCommands.Quote(profile.PiExecutable)); }
        return "$bundleHash=" + SshCommands.Quote(Hash) + "; $packageSize=" + Size.ToString(CultureInfo.InvariantCulture) +
            "; $hostArgs=@(" + string.Join(',', args) + ");\n" + script;
    }

    public async Task SendAsync(TextWriter input, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path);
        // Keep a transfer off the control channel until the bootstrap explicitly requests it.
        // Line framing also works through both cmd.exe and PowerShell OpenSSH default shells.
        var buffer = new byte[24 * 1024];
        var remaining = Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The bundled host archive changed during upload. Retry from the current desktop installation.");
            hash.AppendData(buffer, 0, read);
            await input.WriteLineAsync(Convert.ToBase64String(buffer, 0, read).AsMemory(), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexString(hash.GetHashAndReset()) != Hash)
            throw new IOException("The bundled host archive changed during upload. No host update was activated.");
    }

    public string EncodedBootstrap(SshConnectionProfile profile) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Bootstrap(profile)));
}
