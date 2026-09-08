using System.Text;
using System.Text.Encodings.Web;

namespace PiStation.App.Composition;

internal sealed record AppLaunchOptions
{
    public required string DataRoot { get; init; }

    public string? PiExecutable { get; init; }

    public string? FakePiScenario { get; init; }
    public string? UiTestHostingFixture { get; init; }

    public string? LogFile { get; init; }

    public bool IsUiTest { get; init; }

    public int? UiTestJournalEventLimit { get; init; }

    public int? UiTestTextScalePercent { get; init; }

    public static AppLaunchOptions Parse(string arguments) => ParseTokens(Tokenize(arguments), nameof(arguments));

    public static AppLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return ParseTokens(arguments.ToList(), nameof(arguments));
    }

    private static AppLaunchOptions ParseTokens(List<string> tokens, string parameterName)
    {
        string? dataRoot = null;
        string? piExecutable = null;
        string? fakePiScenario = null;
        string? hostingFixture = null;
        string? logFile = null;
        int? uiTestJournalEventLimit = null;
        int? uiTestTextScalePercent = null;
        var isUiTest = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token)
            {
                case "--ui-test":
                    isUiTest = true;
                    break;
                case "--data-root":
                    dataRoot = ReadValue(tokens, ref index, token);
                    break;
                case "--pi-executable":
                    piExecutable = ReadValue(tokens, ref index, token);
                    break;
                case "--fake-pi-scenario":
                    fakePiScenario = ReadValue(tokens, ref index, token);
                    break;
                case "--ui-test-hosting-fixture":
                    hostingFixture = ReadValue(tokens, ref index, token);
                    break;
                case "--log-file":
                    logFile = ReadValue(tokens, ref index, token);
                    break;
                case "--ui-test-journal-event-limit":
                    var journalLimitValue = ReadValue(tokens, ref index, token);
                    if (!int.TryParse(journalLimitValue, out var parsedJournalLimit) || parsedJournalLimit <= 0)
                    {
                        throw new ArgumentException(
                            "--ui-test-journal-event-limit requires a positive integer.",
                            parameterName);
                    }

                    uiTestJournalEventLimit = parsedJournalLimit;
                    break;
                case "--ui-test-text-scale":
                    var textScaleValue = ReadValue(tokens, ref index, token);
                    if (!int.TryParse(textScaleValue, out var parsedTextScale) ||
                        parsedTextScale is not (100 or 150 or 200))
                    {
                        throw new ArgumentException(
                            "--ui-test-text-scale requires 100, 150, or 200.",
                            parameterName);
                    }

                    uiTestTextScalePercent = parsedTextScale;
                    break;
                default:
                    throw new ArgumentException($"Unknown launch option '{token}'.", parameterName);
            }
        }

        if (fakePiScenario is not null && (!isUiTest || string.IsNullOrWhiteSpace(piExecutable)))
        {
            throw new ArgumentException(
                "--fake-pi-scenario requires --ui-test and --pi-executable.",
                parameterName);
        }

        if (uiTestJournalEventLimit is not null && (!isUiTest || fakePiScenario is null))
        {
            throw new ArgumentException(
                "--ui-test-journal-event-limit requires a FakePi UI-test launch.",
                parameterName);
        }

        if (uiTestTextScalePercent is not null && (!isUiTest || fakePiScenario is null))
        {
            throw new ArgumentException(
                "--ui-test-text-scale requires a FakePi UI-test launch.",
                parameterName);
        }

        if (hostingFixture is not null && (!isUiTest || fakePiScenario is null || dataRoot is null ||
            !Path.GetFullPath(hostingFixture).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--ui-test-hosting-fixture requires FakePi UI-test mode, an explicit data root, and a fixture inside that root.", parameterName);
#if !DEBUG
        if (fakePiScenario is not null || uiTestTextScalePercent is not null || hostingFixture is not null)
        {
            throw new InvalidOperationException("FakePi launch options are disabled outside Debug builds.");
        }
#endif

        return new AppLaunchOptions
        {
            DataRoot = Path.GetFullPath(dataRoot ?? PiStation.Host.HostOptions.DefaultDataRoot),
            PiExecutable = piExecutable is null ? null : Path.GetFullPath(piExecutable),
            FakePiScenario = fakePiScenario,
            UiTestHostingFixture = hostingFixture is null ? null : Path.GetFullPath(hostingFixture),
            LogFile = logFile is null ? null : Path.GetFullPath(logFile),
            IsUiTest = isUiTest,
            UiTestJournalEventLimit = uiTestJournalEventLimit,
            UiTestTextScalePercent = uiTestTextScalePercent,
        };
    }

    public void Log(string message)
    {
        if (LogFile is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(LogFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(
            LogFile,
            $"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"message\":\"" +
            $"{JavaScriptEncoder.Default.Encode(message)}\"}}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadValue(List<string> tokens, ref int index, string option)
    {
        index++;
        if (index >= tokens.Count || string.IsNullOrWhiteSpace(tokens[index]))
        {
            throw new ArgumentException($"{option} requires a value.", nameof(tokens));
        }

        return tokens[index];
    }

    private static List<string> Tokenize(string arguments)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quoted)
        {
            throw new ArgumentException("Launch arguments contain an unterminated quote.", nameof(arguments));
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
