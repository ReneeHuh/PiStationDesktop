using System.Diagnostics;
using System.Text;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Process;

public static class PiProcessLauncher
{
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(false, true);

    public static async Task<PiProcess> StartAsync(
        PiProcessLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        var sessionDirectory = Path.GetFullPath(options.SessionDirectory);
        Directory.CreateDirectory(sessionDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Installation.ExecutablePath,
            WorkingDirectory = projectDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = StrictUtf8WithoutBom,
            StandardInputEncoding = StrictUtf8WithoutBom,
            StandardOutputEncoding = StrictUtf8WithoutBom,
            UseShellExecute = false,
        };

        foreach (var argument in options.Installation.LaunchPrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("rpc");
        startInfo.ArgumentList.Add("--session-dir");
        startInfo.ArgumentList.Add(sessionDirectory);
        startInfo.ArgumentList.Add("--session-id");
        startInfo.ArgumentList.Add(options.SessionId);
        startInfo.ArgumentList.Add("--no-extensions");

        foreach (var argument in options.AdditionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var variable in options.EnvironmentVariables)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new PiRpcConnectionException(
                    $"Failed to start Pi executable '{options.Installation.ExecutablePath}'.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        var connection = new PiRpcConnection(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            options.ConnectionOptions);
        connection.Start();

        var ownedProcess = new PiProcess(
            process,
            connection,
            options.ShutdownTimeout,
            options.StandardErrorCharacterLimit);

        try
        {
            await connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
            return ownedProcess;
        }
        catch (Exception exception)
        {
            var stderr = ownedProcess.StandardError;
            await ownedProcess.DisposeAsync().ConfigureAwait(false);
            var suffix = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $" Stderr: {stderr}";
            throw new PiRpcConnectionException($"Pi process did not become ready.{suffix}", exception);
        }
    }

    private static void ValidateOptions(PiProcessLaunchOptions options)
    {
        if (!File.Exists(options.Installation.ExecutablePath))
        {
            throw new FileNotFoundException("Pi executable does not exist.", options.Installation.ExecutablePath);
        }

        if (!Directory.Exists(options.ProjectDirectory))
        {
            throw new DirectoryNotFoundException($"Pi project directory does not exist: {options.ProjectDirectory}");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SessionId);
        if (options.SessionId.Any(char.IsControl))
        {
            throw new ArgumentException("Pi session id contains control characters.", nameof(options));
        }
    }
}
