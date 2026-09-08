using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiStation.HostingVerification;

internal sealed class LiveVerificationReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<object> _checks = [];
    private readonly string _path;
    private readonly string _repository;
    public LiveVerificationReport(string path, string repository, bool resume = false)
    {
        _path = path;
        _repository = repository;
        if (!resume) return;
        using var previous = JsonDocument.Parse(File.ReadAllText(path));
        if (previous.RootElement.GetProperty("repository").GetString() != repository) throw new ArgumentException("The saved report belongs to another repository.");
        _checks.AddRange(previous.RootElement.GetProperty("checks").EnumerateArray().Select(check => (object)check.Clone()));
        if (!WasPassed("isolated-repository") || !WasPassed("delete-own-comments")) throw new ArgumentException("Continuation requires the completed fixture and comment checks from this runner.");
    }

    public bool WasPassed(string name) => _checks.Select(check => JsonSerializer.SerializeToElement(check))
        .Any(check => check.GetProperty("name").GetString() == name && check.GetProperty("status").GetString() == "passed");
    public void Record(string name, string status, string detail)
    {
        _checks.Add(new { name, status, detail });
        var current = _checks.Select(check => JsonSerializer.SerializeToElement(check))
            .GroupBy(check => check.GetProperty("name").GetString(), StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var summary = current.GroupBy(check => check.GetProperty("status").GetString()!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        File.WriteAllText(_path, JsonSerializer.Serialize(new { repository = _repository, updatedUtc = DateTimeOffset.UtcNow, summary, currentChecks = current, checks = _checks }, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { check = name, status, detail }));
    }

    public async Task CheckAsync(string name, Func<Task> test)
    {
        if (WasPassed(name)) return;
        try { await test(); Record(name, "passed", "Verified against GitHub."); }
        catch (Exception exception) { Record(name, "failed", exception.Message); throw; }
    }
}

internal sealed class LiveGitHub(string workingDirectory, CancellationToken cancellationToken)
{
    public const string Description = "Pi Station generated verification fixture. Contains no user source.";

    public static void ValidateNewRepository(string repository)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9-]*/pistation-verification-[a-z0-9][a-z0-9-]{0,70}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Live writes require a new owner/pistation-verification-* repository; existing repositories are never reused.");
    }

    public Task<string> CliAsync(params string[] arguments)
    {
        var tool = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe") : "gh";
        return ProcessAsync(tool, arguments);
    }

    public Task<string> GitAsync(params string[] arguments) => ProcessAsync("git", arguments);

    public async Task<JsonElement> ApiAsync(string method, string endpoint, object? body = null)
    {
        var tool = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe") : "gh";
        var arguments = new List<string> { "api", "--hostname", "github.com", "--method", method, endpoint };
        if (body is not null) arguments.AddRange(["--input", "-"]);
        var output = await ProcessAsync(tool, arguments, body is null ? null : JsonSerializer.Serialize(body));
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(output) ? "{}" : output);
        return document.RootElement.Clone();
    }

    private async Task<string> ProcessAsync(string executable, IReadOnlyList<string> arguments, string? input = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {error.Trim()}");
            return output.Trim();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await ((Task)Task.WhenAll(stdout, stderr)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
    }
}
