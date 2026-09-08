using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PiStation.ClientRuntime;
using PiStation.Host;
using PiStation.Host.Hosting;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Models;
using PiStation.HostingVerification;

if (args is ["write-live", var writeRoot, var writeRepository])
{
    await LiveWriteVerification.RunAsync(writeRoot, writeRepository);
    return;
}
if (args is ["continue-live", var continueRoot, var continueRepository])
{
    await LiveWriteVerification.RunAsync(continueRoot, continueRepository, resume: true);
    return;
}

if (args is ["check-cli"])
{
    Console.WriteLine(JsonSerializer.Serialize(await SourceControlHostingService.GetToolDiagnosticsAsync()));
    return;
}
if (args.Length < 2 || args[0] is not ("prepare-ui" or "read-live"))
    throw new ArgumentException("Usage: check-cli OR prepare-ui <new-run-directory> OR read-live <new-run-directory> <owner/repository> <pull-request-number> OR write-live <new-run-directory> <owner/pistation-verification-new-name> OR continue-live <existing-run-directory> <owner/pistation-verification-name>");
var live = args[0] == "read-live";
if (args.Length != (live ? 4 : 2)) throw new ArgumentException("Provide exactly the arguments for the selected verification mode.");
string? number = null;
if (live)
{
    if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedNumber) || parsedNumber <= 0)
        throw new ArgumentException("Use a positive pull request number.");
    number = parsedNumber.ToString(CultureInfo.InvariantCulture);
}
var repository = live ? args[2] : "pistation-fixture/review";
var segments = repository.Split('/');
if (segments.Length != 2 || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
    throw new ArgumentException("Use an owner/repository name.");
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Use a new empty verification directory.");
var projectPath = Path.Combine(root, "project");
var dataRoot = Path.Combine(root, "data");
Directory.CreateDirectory(projectPath);
Directory.CreateDirectory(dataRoot);
await GitAsync("init", "--quiet", "--initial-branch=main");
await GitAsync("remote", "add", "origin", "https://github.com/" + repository + ".git");
var fixturePath = Path.Combine(dataRoot, "hosting-fixture.json");
if (!live) File.Copy(Path.Combine(AppContext.BaseDirectory, "pull-request-review.json"), fixturePath);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await using var host = await EmbeddedEnvironmentHost.StartAsync(new HostOptions { ApplicationDataRoot = dataRoot, EnvironmentName = "Hosting verification" }, cancellationToken: timeout.Token);
await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
await client.ConnectAsync(timeout.Token);
var project = await client.AddProjectAsync(new(projectPath, live ? "Live GitHub verification" : "GitHub UI fixture"), timeout.Token);
if (live)
{
    await LiveReadVerification.RunAsync(client, new(project.ProjectId), repository, number!, root, timeout.Token);
}
else Console.WriteLine(JsonSerializer.Serialize(new { mode = "ui-fixture", dataRoot, projectPath, fixturePath, project.ProjectId }));

async Task GitAsync(params string[] arguments)
{
    var info = new ProcessStartInfo("git") { WorkingDirectory = projectPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await output;
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
}
