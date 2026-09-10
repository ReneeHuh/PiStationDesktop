using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PiStation.ClientRuntime;

public sealed record PreparedPiSessionShare(string Path, string Sha256, long Bytes);

public static class PiSessionShare
{
    public static async Task<PreparedPiSessionShare> PrepareAsync(string path, CancellationToken token = default)
    {
        await using var file = File.OpenRead(path);
        if (file.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Share exports must be between 1 byte and 8 MiB. Save larger sessions locally.");
        return new(System.IO.Path.GetFullPath(path), Convert.ToHexString(await SHA256.HashDataAsync(file, token)), file.Length);
    }

    public static ProcessStartInfo CreateStartInfo(PreparedPiSessionShare share, string title, bool isPublic)
    {
        var start = new ProcessStartInfo("gh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "gist", "create", "--desc", title[..Math.Min(title.Length, 256)], "--filename", "session.html" }) start.ArgumentList.Add(argument);
        if (isPublic) start.ArgumentList.Add("--public");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(share.Path);
        start.Environment["GH_HOST"] = "github.com";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        return start;
    }

    public static async Task<Uri> PublishAsync(PreparedPiSessionShare share, string title, bool isPublic, CancellationToken token = default)
    {
        // Hold a read-only lease so the reviewed file cannot change while gh reads it.
        await using var file = new FileStream(share.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length != share.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != share.Sha256)
            throw new InvalidDataException("The reviewed export changed. Prepare and review it again.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = new Process { StartInfo = CreateStartInfo(share, title, isPublic) };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Install GitHub CLI and run gh auth login on this PC before sharing."); }
        process.StandardInput.Close();
        using var stop = lifetime.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, lifetime.Token);
            var error = ReadBoundedAsync(process.StandardError, lifetime.Token);
            await process.WaitForExitAsync(lifetime.Token);
            var stdout = await output;
            var stderr = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("GitHub did not confirm sharing: " + stderr.Trim()[..Math.Min(stderr.Trim().Length, 1000)] + " Check your gists before retrying.");
            if (!Uri.TryCreate(stdout.Trim(), UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "gist.github.com" || url.UserInfo.Length > 0)
                throw new InvalidOperationException("GitHub returned no recognizable gist URL. Check your gists before retrying.");
            return url;
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("Sharing was interrupted. A gist may have been created; check your GitHub gists before retrying."); }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer, token) is var count && count > 0)
            if (result.Length < 16 * 1024) result.Append(buffer, 0, Math.Min(count, 16 * 1024 - result.Length));
        return result.ToString();
    }
}
