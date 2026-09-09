using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly string _commandLog;
    private readonly FakePiArguments _arguments;
    private readonly TextWriter _error;
    private readonly Stream _input;
    private readonly List<(string Id, string Command)> _outOfOrderRequests = [];
    private readonly FakeSessionStore _session;
    private readonly CancellationTokenSource _stop = new();
    private readonly FakeJsonlWriter _writer;
    private int _exitCode;
    private string _modelId = "fake-standard";
    private string _modelProvider = "fake";
    private string? _pendingDialogPromptId;
    private string? _pendingInteractionPrompt;
    private bool _retryDelayWasAborted;
    private bool _isStreaming;
    private readonly List<string> _queuedSteering = [];
    private readonly List<string> _queuedFollowUp = [];
    private string _steeringMode = "all";
    private string _followUpMode = "all";
    private string _thinkingLevel = "off";
    private bool _autoCompactionEnabled = true;
    private bool _autoRetryEnabled = true;
    private string? _sessionName;

    public FakePiServer(Stream input, Stream output, TextWriter error, FakePiArguments arguments)
    {
        _input = input;
        _writer = new FakeJsonlWriter(output);
        _error = error;
        _arguments = arguments;
        _session = new FakeSessionStore(arguments.SessionDirectory, arguments.SessionId);
        _commandLog = Path.Combine(arguments.SessionDirectory, "command-log.jsonl");
        var management = ReadManagementSettings();
        _resourceEnabledAtStart = management["enabled"]?.GetValue<bool>() ?? true;
        _projectTrustedAtStart = management["trusted"]?.GetValue<bool>() ?? false;
    }

    public async Task<int> RunAsync()
    {
        if (_arguments.Scenario == "extension-ui")
        {
            foreach (var update in new[]
            {
                new JsonObject { ["method"] = "notify", ["message"] = "Extension connected", ["notifyType"] = "info" },
                new JsonObject { ["method"] = "setStatus", ["statusKey"] = "probe", ["statusText"] = "Ready to review" },
                new JsonObject { ["method"] = "setWidget", ["widgetKey"] = "above", ["widgetLines"] = new JsonArray("Review changes", "Run focused checks") },
                new JsonObject { ["method"] = "setWidget", ["widgetKey"] = "below", ["widgetLines"] = new JsonArray("Extension footer"), ["widgetPlacement"] = "belowEditor" },
                new JsonObject { ["method"] = "setTitle", ["title"] = "Review assistant" },
                new JsonObject { ["method"] = "set_editor_text", ["text"] = "Review this project using $skill:fake-skill." },
            })
            {
                update["type"] = "extension_ui_request";
                update["id"] = Guid.NewGuid().ToString();
                await _writer.WriteAsync(update, cancellationToken: _stop.Token).ConfigureAwait(false);
            }
        }
        if (_arguments.Scenario == "startup-crash")
        {
            await _error.WriteLineAsync("FakePi startup crash.").ConfigureAwait(false);
            return 41;
        }

        if (_arguments.Scenario == "stderr-flood")
        {
            await _error.WriteAsync(new string('x', 100_000)).ConfigureAwait(false);
            await _error.WriteLineAsync("stderr-tail-marker").ConfigureAwait(false);
            await _error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await foreach (var line in FakeJsonlReader.ReadAllAsync(_input, _stop.Token).ConfigureAwait(false))
            {
                JsonObject command;
                try
                {
                    command = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Command must be an object.");
                }
                catch (JsonException exception)
                {
                    await _writer.WriteAsync(new JsonObject
                    {
                        ["type"] = "response",
                        ["command"] = "parse",
                        ["success"] = false,
                        ["error"] = $"Failed to parse command: {exception.Message}",
                    }).ConfigureAwait(false);
                    continue;
                }

                await HandleCommandAsync(command, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }

        return _exitCode;
    }

    private async Task HandleCommandAsync(JsonObject command, CancellationToken cancellationToken)
    {
        var type = command["type"]?.GetValue<string>() ?? string.Empty;
        var id = command["id"]?.GetValue<string>();
        if (type == "extension_ui_response")
        {
            await HandleExtensionResponseAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (id is null)
        {
            await _writer.WriteAsync(Error(null, type, "Missing request id."), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await AppendCommandLogAsync(type, command, cancellationToken).ConfigureAwait(false);

        if (type == "get_state")
        {
            if (await TryEmitFramingFailureAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await WriteStateAsync(id, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (type)
        {
            case "bash":
                _ = HandleBashAsync(id, command, cancellationToken);
                break;
            case "abort_bash":
                _bashCancelled?.TrySetResult();
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "prompt":
                if (command["message"]?.ToString() is { } sessionPrompt && sessionPrompt.StartsWith("/pistation-desktop-sessions ", StringComparison.Ordinal))
                {
                    await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                    _ = HandleSessionNavigationAsync(sessionPrompt, cancellationToken);
                    break;
                }
                if (_arguments.Scenario == "agent-workflow" && command["message"]?.ToString() is { } agentPrompt)
                {
                    if (agentPrompt.StartsWith("/pistation-desktop-agents ", StringComparison.Ordinal)) await HandleAgentsCommandAsync(id, agentPrompt, cancellationToken);
                    else await HandleAgentPromptAsync(id, agentPrompt, cancellationToken);
                    break;
                }
                if (_arguments.Scenario == "plan-workflow" && command["message"]?.ToString() is { } planPrompt)
                {
                    if (planPrompt.StartsWith("/pistation-desktop-plan ", StringComparison.Ordinal))
                        await HandlePlanCommandAsync(id, planPrompt, cancellationToken).ConfigureAwait(false);
                    else await HandlePlanPromptAsync(id, planPrompt, cancellationToken).ConfigureAwait(false);
                    break;
                }
                if (_arguments.Scenario.StartsWith("resource-management", StringComparison.Ordinal) &&
                    command["message"]?.GetValue<string>() is { } managementPrompt &&
                    managementPrompt.StartsWith("/pistation-desktop-resources ", StringComparison.Ordinal))
                {
                    await HandleManagementAsync(id, managementPrompt, cancellationToken).ConfigureAwait(false);
                    break;
                }
                if (_arguments.Scenario == "extension-ui" && command["message"]?.GetValue<string>() == "/review")
                {
                    await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
                await HandlePromptAsync(
                        id,
                        command["message"]?.GetValue<string>() ?? string.Empty,
                        command["streamingBehavior"]?.GetValue<string>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "get_entries":
                if (_arguments.Scenario == "out-of-order")
                {
                    await QueueOutOfOrderAsync(id, type, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteEntriesAsync(id, command["since"]?.GetValue<string>(), cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
            case "fork":
                var entryId = command["entryId"]?.GetValue<string>();
                var forked = entryId is not null &&
                    await _session.RewindAsync(entryId, cancellationToken).ConfigureAwait(false);
                if (!forked)
                {
                    await _writer.WriteAsync(
                        Error(id, type, $"Entry not found: {entryId}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }

                await _writer.WriteAsync(Response(id, type, new JsonObject
                {
                    ["text"] = string.Empty,
                    ["cancelled"] = false,
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "new_session":
                await _session.RewindAsync(null, cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(Response(id, type, new JsonObject
                {
                    ["cancelled"] = false,
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "get_available_models":
                await _writer.WriteAsync(Response(id, type, new JsonObject
                {
                    ["models"] = new JsonArray(
                        Model("fake", "fake-standard", "Fake Standard", supportsReasoning: true),
                        Model("fake", "fake-fast", "Fake Fast", supportsReasoning: false)),
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "get_commands":
                var skillDirectory = Path.Combine(_arguments.SessionDirectory, "skills", "fake-skill");
                Directory.CreateDirectory(skillDirectory);
                var skillPath = Path.Combine(skillDirectory, "SKILL.md");
                if (!File.Exists(skillPath)) await File.WriteAllTextAsync(skillPath, "---\nname: fake-skill\ndescription: Test resource\n---\nFAKE_SKILL_INSTRUCTION: verify the actual prompt body.", cancellationToken).ConfigureAwait(false);
                var commands = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "review",
                            ["description"] = "Review the current changes",
                            ["source"] = "extension",
                            ["sourceInfo"] = new JsonObject { ["path"] = Path.Combine(_arguments.SessionDirectory, "extensions", "review.ts"), ["source"] = "local", ["scope"] = "user", ["origin"] = "top-level" },
                        },
                        new JsonObject
                        {
                            ["name"] = "release-notes",
                            ["description"] = "Draft release notes",
                            ["source"] = "prompt",
                            ["sourceInfo"] = new JsonObject { ["path"] = Path.Combine(_arguments.SessionDirectory, "prompts", "release-notes.md"), ["source"] = "local", ["scope"] = "project", ["origin"] = "top-level" },
                        },
                        new JsonObject
                        {
                            ["name"] = "skill:fake-skill",
                            ["description"] = "Exercise the fake skill",
                            ["source"] = "skill",
                            ["sourceInfo"] = new JsonObject { ["path"] = skillPath, ["source"] = "local", ["scope"] = "user", ["origin"] = "top-level", ["baseDir"] = skillDirectory },
                        });
                if (_arguments.Scenario.StartsWith("resource-management", StringComparison.Ordinal)) commands.Add(new JsonObject
                {
                    ["name"] = "pistation-desktop-resources", ["source"] = "extension",
                    ["sourceInfo"] = new JsonObject { ["path"] = Path.Combine(_arguments.SessionDirectory, "management.ts"), ["scope"] = "temporary" },
                });
                if (_arguments.Scenario != "navigation-unavailable") commands.Add(new JsonObject { ["name"] = "pistation-desktop-sessions", ["source"] = "extension" });
                if (_arguments.Scenario == "plan-workflow") commands.Add(new JsonObject { ["name"] = "pistation-desktop-plan", ["source"] = "extension" });
                if (_arguments.Scenario == "agent-workflow") commands.Add(new JsonObject { ["name"] = "pistation-desktop-agents", ["source"] = "extension" });
                await _writer.WriteAsync(Response(id, type, new JsonObject { ["commands"] = commands }), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "compact" when _arguments.Scenario != "command-timeout":
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "compaction_start",
                    ["reason"] = "manual",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                var compactionResult = new JsonObject
                {
                    ["summary"] = "Fake compacted context summary.",
                    ["firstKeptEntryId"] = null,
                    ["tokensBefore"] = 1200,
                    ["estimatedTokensAfter"] = 320,
                    ["usage"] = new JsonObject
                    {
                        ["input"] = 1200,
                        ["output"] = 80,
                        ["cacheRead"] = 40,
                        ["cacheWrite"] = 10,
                        ["totalTokens"] = 1330,
                        ["cost"] = new JsonObject { ["total"] = 0.0125m },
                    },
                };
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "compaction_end",
                    ["result"] = compactionResult.DeepClone(),
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(Response(id, type, compactionResult), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "set_session_name":
                _sessionName = command["name"]?.GetValue<string>();
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "set_model":
                var provider = command["provider"]?.GetValue<string>();
                var modelId = command["modelId"]?.GetValue<string>();
                if (provider != "fake" || modelId is not ("fake-standard" or "fake-fast"))
                {
                    await _writer.WriteAsync(
                        Error(id, type, $"Model not found: {provider}/{modelId}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }

                _modelProvider = provider;
                _modelId = modelId;
                if (_modelId == "fake-fast")
                {
                    _thinkingLevel = "off";
                }

                await _writer.WriteAsync(
                    Response(
                        id,
                        type,
                        Model(
                            _modelProvider,
                            _modelId,
                            _modelId == "fake-standard" ? "Fake Standard" : "Fake Fast",
                            _modelId == "fake-standard")),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "get_available_thinking_levels":
                await _writer.WriteAsync(Response(id, type, new JsonObject
                {
                    ["levels"] = new JsonArray(
                        AvailableThinkingLevels().Select(static level => JsonValue.Create(level)).ToArray()),
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "set_auto_compaction":
            case "set_auto_retry":
                if (_arguments.Scenario == "automation-rejected" && type == "set_auto_retry")
                {
                    await _writer.WriteAsync(Error(id, type, "Injected automation rejection"), cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
                var enabled = command["enabled"]!.GetValue<bool>();
                if (type == "set_auto_compaction") _autoCompactionEnabled = enabled;
                else _autoRetryEnabled = enabled;
                await File.WriteAllTextAsync(Path.Combine(_arguments.SessionDirectory, "automation-" + _arguments.SessionId + ".json"),
                    new JsonObject { ["compaction"] = _autoCompactionEnabled, ["retry"] = _autoRetryEnabled }.ToJsonString(), cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "set_thinking_level":
                var level = command["level"]?.GetValue<string>();
                if (level is null || !AvailableThinkingLevels().Contains(level, StringComparer.Ordinal))
                {
                    await _writer.WriteAsync(
                        Error(id, type, $"Thinking level not supported: {level}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }

                _thinkingLevel = level;
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "get_last_assistant_text" when _arguments.Scenario == "out-of-order":
                await QueueOutOfOrderAsync(id, type, cancellationToken).ConfigureAwait(false);
                break;
            case "clear_queue":
                var clearedSteering = _arguments.Scenario == "queue"
                    ? _queuedSteering.ToArray()
                    : ["queued steering"];
                var clearedFollowUp = _arguments.Scenario == "queue"
                    ? _queuedFollowUp.ToArray()
                    : ["queued follow-up"];
                _queuedSteering.Clear();
                _queuedFollowUp.Clear();
                await _writer.WriteAsync(Response(id, type, new JsonObject
                {
                    ["steering"] = new JsonArray(clearedSteering
                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["followUp"] = new JsonArray(clearedFollowUp
                        .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (_arguments.Scenario == "queue")
                {
                    await WriteQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            case "set_steering_mode":
                _steeringMode = command["mode"]?.GetValue<string>() ?? _steeringMode;
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "set_follow_up_mode":
                _followUpMode = command["mode"]?.GetValue<string>() ?? _followUpMode;
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
            case "abort":
                _fakeNavigationCancelled?.TrySetResult();
                await HandleAbortAsync(id, cancellationToken).ConfigureAwait(false);
                break;
            case "abort_retry":
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                _retryDelayWasAborted = true;
                break;
            case "compact" when _arguments.Scenario == "command-timeout":
                break;
            case "get_last_assistant_text" when _arguments.Scenario == "command-timeout":
                break;
            default:
                await _writer.WriteAsync(Response(id, type), cancellationToken: cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandlePromptAsync(
        string id,
        string message,
        string? streamingBehavior,
        CancellationToken cancellationToken)
    {
        if (_arguments.Scenario == "queue")
        {
            await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (streamingBehavior == "steer")
            {
                _queuedSteering.Add(message);
                await WriteQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (streamingBehavior == "followUp")
            {
                _queuedFollowUp.Add(message);
                await WriteQueueUpdateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _isStreaming = true;
            await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        switch (_arguments.Scenario)
        {
            case "source-control-writer":
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
                var generated = FakeSessionStore.AssistantMessage("{\"title\":\"Fix the selected behavior\",\"body\":\"Explain the implementation change\"}");
                await _writer.WriteAsync(new JsonObject { ["type"] = "message_end", ["message"] = generated.DeepClone() }, cancellationToken: cancellationToken).ConfigureAwait(false);
                await WriteSettlementAsync(generated, cancellationToken).ConfigureAwait(false);
                return;
            case "command-timeout":
                return;
            case "crash":
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
                await _error.WriteLineAsync("FakePi crashed after prompt acceptance.").ConfigureAwait(false);
                await _error.FlushAsync(cancellationToken).ConfigureAwait(false);
                _exitCode = 23;
                _stop.Cancel();
                return;
            case "crash-once" when MarkFirstCrash():
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
                await _error.WriteLineAsync("FakePi crashed once after prompt acceptance.").ConfigureAwait(false);
                await _error.FlushAsync(cancellationToken).ConfigureAwait(false);
                _exitCode = 23;
                _stop.Cancel();
                return;
            case "dialog":
                _pendingDialogPromptId = id;
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "extension_ui_request",
                    ["id"] = "dialog-1",
                    ["method"] = "confirm",
                    ["title"] = "Allow operation?",
                    ["message"] = "Fake blocking dialog",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            case "interactions":
                _pendingInteractionPrompt = message;
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "extension_ui_request",
                    ["id"] = "approval-1",
                    ["method"] = "confirm",
                    ["title"] = "Allow test operation?",
                    ["message"] = "Pi needs approval before continuing.",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            case "stop":
            case "stop-retry-delay":
                await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "auto_retry_start",
                    ["attempt"] = 1,
                    ["maxAttempts"] = 3,
                    ["delayMs"] = 5000,
                    ["errorMessage"] = "temporary failure",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            case "ui-tool" when message == "Render the Markdown fixture":
                await EmitMarkdownTurnAsync(id, message, cancellationToken).ConfigureAwait(false);
                return;
            case "ui-input":
                await EmitInputTurnAsync(id, message, cancellationToken).ConfigureAwait(false);
                return;
            case "tool":
            case "ui-tool":
            case "ui-resync":
            case "large-tool":
                await EmitToolTurnAsync(id, message, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await EmitNormalTurnAsync(id, message, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task EmitNormalTurnAsync(
        string id,
        string prompt,
        CancellationToken cancellationToken,
        bool promptWasAccepted = false)
    {
        const string answer = "Hello from Fake Pi 👽";
        if (!promptWasAccepted)
        {
            await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = FakeSessionStore.AssistantMessage(string.Empty),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_start", 0, null, cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_delta", 0, "Hello", cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_delta", 0, " from Fake Pi 👽", cancellationToken, split: true)
            .ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_end", 0, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_end",
            ["message"] = FakeSessionStore.AssistantMessage(answer),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteSettlementAsync(FakeSessionStore.AssistantMessage(answer), cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitMarkdownTurnAsync(
        string id,
        string prompt,
        CancellationToken cancellationToken)
    {
        const string answer = """
            # Markdown fixture

            Native **bold**, *italic*, and `inline code` with a [safe link](https://example.com).

            > Quoted guidance stays visually distinct.

            1. First numbered item
            2. Second numbered item

            ```csharp
            public static string Greet(string name)
            {
                return $"Hello, {name}!";
            }
            ```

            <script>alert('not executed')</script>
            """;
        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = FakeSessionStore.AssistantMessage(string.Empty),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_start", 0, null, cancellationToken).ConfigureAwait(false);
        var splitIndex = answer.IndexOf("```csharp", StringComparison.Ordinal);
        await WriteMessageDeltaAsync("text_delta", 0, answer[..splitIndex], cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_delta", 0, answer[splitIndex..], cancellationToken, split: true)
            .ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_end", 0, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_end",
            ["message"] = FakeSessionStore.AssistantMessage(answer),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteSettlementAsync(FakeSessionStore.AssistantMessage(answer), cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitInputTurnAsync(
        string id,
        string prompt,
        CancellationToken cancellationToken)
    {
        var transcriptMessages = Enumerable.Range(1, 36)
            .Select(index => $"Transcript line {index:00}: deterministic scrolling content.");
        var answers = new List<string> { "Long transcript head marker" };
        answers.AddRange(transcriptMessages);
        answers.Add("Long transcript tail marker");
        var answer = string.Join("\n\n", answers);

        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await WaitForUiToolGateAsync("input-ready", cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        JsonObject? finalMessage = null;
        foreach (var part in answers)
        {
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = FakeSessionStore.AssistantMessage(string.Empty),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("text_start", 0, null, cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("text_delta", 0, part, cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("text_end", 0, part, cancellationToken).ConfigureAwait(false);
            finalMessage = FakeSessionStore.AssistantMessage(part);
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "message_end",
                ["message"] = finalMessage.DeepClone(),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        finalMessage ??= FakeSessionStore.AssistantMessage(string.Empty);
        await WriteSettlementAsync(finalMessage, cancellationToken).ConfigureAwait(false);
    }

    private bool MarkFirstCrash()
    {
        var marker = Path.Combine(_arguments.SessionDirectory, $".{_arguments.SessionId}.crash-once");
        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            return false;
        }
    }

    private async Task EmitToolTurnAsync(string id, string prompt, CancellationToken cancellationToken)
    {
        const string answer = "Tool finished.";
        const string reasoning = "I’ll inspect the request, run the command, and verify the result.";
        var exposeIntermediateStates = _arguments.Scenario is "ui-tool" or "ui-resync";
        var firstToolContentIndex = exposeIntermediateStates ? 1 : 0;
        var textContentIndex = exposeIntermediateStates ? 3 : 1;
        var toolOutput = _arguments.Scenario == "large-tool"
            ? new string('x', 4096) + "tail-marker"
            : "one\ntwo\ncomplete";
        await _writer.WriteAsync(Response(id, "prompt"), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "turn_start" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = FakeSessionStore.AssistantMessage(string.Empty),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exposeIntermediateStates)
        {
            await WriteMessageDeltaAsync("thinking_start", 0, null, cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("thinking_delta", 0, reasoning, cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("thinking_end", 0, reasoning, cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteAsync(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_start",
            ["contentIndex"] = firstToolContentIndex,
            ["id"] = "call-1",
            ["toolName"] = "bash",
        }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_delta",
            ["contentIndex"] = firstToolContentIndex,
            ["delta"] = "{\"command\":\"echo hi\"}",
        }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_end",
            ["contentIndex"] = firstToolContentIndex,
            ["toolCall"] = ToolCall(),
        }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "tool_execution_start",
            ["toolCallId"] = "call-1",
            ["toolName"] = "bash",
            ["args"] = new JsonObject { ["command"] = "echo hi" },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exposeIntermediateStates)
        {
            await WaitForUiToolGateAsync("continue-tool", cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        }

        if (_arguments.Scenario == "large-tool")
        {
            await WriteToolUpdateAsync(toolOutput, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteToolUpdateAsync("one", cancellationToken).ConfigureAwait(false);
            await WriteToolUpdateAsync("one\ntwo", cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "tool_execution_end",
            ["toolCallId"] = "call-1",
            ["toolName"] = "bash",
            ["result"] = ToolResult(toolOutput),
            ["isError"] = false,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (exposeIntermediateStates)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
        }

        if (exposeIntermediateStates)
        {
            await _writer.WriteAsync(MessageUpdate(new JsonObject
            {
                ["type"] = "toolcall_start",
                ["contentIndex"] = 2,
                ["id"] = "call-2",
                ["toolName"] = "read",
            }), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(MessageUpdate(new JsonObject
            {
                ["type"] = "toolcall_delta",
                ["contentIndex"] = 2,
                ["delta"] = "{\"path\":\"README.md\"}",
            }), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(MessageUpdate(new JsonObject
            {
                ["type"] = "toolcall_end",
                ["contentIndex"] = 2,
                ["toolCall"] = ToolCall("call-2", "read", "path", "README.md"),
            }), cancellationToken: cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "tool_execution_start",
                ["toolCallId"] = "call-2",
                ["toolName"] = "read",
                ["args"] = new JsonObject { ["path"] = "README.md" },
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "tool_execution_end",
                ["toolCallId"] = "call-2",
                ["toolName"] = "read",
                ["result"] = ToolResult("README.md loaded"),
                ["isError"] = false,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        }

        await WriteMessageDeltaAsync("text_start", textContentIndex, null, cancellationToken).ConfigureAwait(false);
        if (exposeIntermediateStates)
        {
            await WriteMessageDeltaAsync("text_delta", textContentIndex, "Tool", cancellationToken).ConfigureAwait(false);
            await WaitForUiToolGateAsync("complete-turn", cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            await WriteMessageDeltaAsync("text_delta", textContentIndex, " finished.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteMessageDeltaAsync("text_delta", textContentIndex, answer, cancellationToken).ConfigureAwait(false);
        }

        await WriteMessageDeltaAsync("text_end", textContentIndex, answer, cancellationToken).ConfigureAwait(false);
        var finalContent = new JsonArray();
        if (exposeIntermediateStates)
        {
            finalContent.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning });
        }

        finalContent.Add(ToolCall());
        if (exposeIntermediateStates)
        {
            finalContent.Add(ToolCall("call-2", "read", "path", "README.md"));
        }

        finalContent.Add(new JsonObject { ["type"] = "text", ["text"] = answer });
        var finalMessage = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = finalContent,
            ["usage"] = FakeSessionStore.Usage(),
        };
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_end",
            ["message"] = finalMessage.DeepClone(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteSettlementAsync(finalMessage, cancellationToken).ConfigureAwait(false);
        if (_arguments.Scenario == "ui-resync")
        {
            await File.WriteAllTextAsync(
                Path.Combine(_arguments.SessionDirectory, $".{_arguments.SessionId}.settled"),
                "settled",
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteSettlementAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "turn_end",
            ["message"] = message.DeepClone(),
            ["toolResults"] = new JsonArray(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "agent_end",
            ["messages"] = new JsonArray(message.DeepClone()),
            ["willRetry"] = false,
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject { ["type"] = "agent_settled" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WaitForUiToolGateAsync(string gate, CancellationToken cancellationToken)
    {
        // Only the UI fixture opts into these gates. Keep each observable state
        // stable until the external UI driver has asserted it and can advance.
        var gateDirectory = Path.Combine(Environment.CurrentDirectory, ".pistation-ui-tool-gates");
        if (_arguments.Scenario is not ("ui-tool" or "ui-input") || !Directory.Exists(gateDirectory))
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (!File.Exists(Path.Combine(gateDirectory, gate)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task HandleAbortAsync(string id, CancellationToken cancellationToken)
    {
        if (_arguments.Scenario == "stop-retry-delay" && !_retryDelayWasAborted)
        {
            return;
        }

        await _writer.WriteAsync(Response(id, "abort"), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_arguments.Scenario == "agent-workflow" && _isStreaming)
        {
            foreach (var child in _agentResults.Where(child => child!["status"]!.ToString() == "running"))
            { child!["status"] = "interrupted"; child["stopReason"] = "aborted"; child["exitCode"] = 1; }
            await FinishAgentWorkflowAsync(cancellationToken);
        }
        if (_arguments.Scenario is "stop" or "stop-retry-delay" or "queue")
        {
            if (_arguments.Scenario is "stop" or "stop-retry-delay")
            {
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "auto_retry_end",
                    ["success"] = false,
                    ["attempt"] = 1,
                    ["finalError"] = "aborted",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            _isStreaming = false;
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "agent_end",
                ["messages"] = new JsonArray(),
                ["willRetry"] = false,
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject { ["type"] = "agent_settled" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleExtensionResponseAsync(JsonObject command, CancellationToken cancellationToken)
    {
        if (_arguments.Scenario == "dialog" &&
            command["id"]?.GetValue<string>() == "dialog-1" &&
            command["cancelled"]?.GetValue<bool>() == true &&
            _pendingDialogPromptId is not null)
        {
            var marker = Path.Combine(_arguments.SessionDirectory, "dialog-cancelled.txt");
            await File.WriteAllTextAsync(marker, "cancelled", Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            var promptId = _pendingDialogPromptId;
            _pendingDialogPromptId = null;
            await EmitNormalTurnAsync(promptId, "dialog prompt", cancellationToken, promptWasAccepted: true)
                .ConfigureAwait(false);
            return;
        }

        if (_arguments.Scenario != "interactions" || _pendingInteractionPrompt is null)
        {
            return;
        }

        var interactionId = command["id"]?.GetValue<string>();
        if (interactionId == "approval-1" && command["confirmed"]?.GetValue<bool>() == true)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_arguments.SessionDirectory, "approval-response.txt"),
                "approved",
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject
            {
                ["type"] = "extension_ui_request",
                ["id"] = "question-1",
                ["method"] = "select",
                ["title"] = "Choose the next action",
                ["options"] = new JsonArray("Run tests", "Skip tests"),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        if (interactionId != "question-1" || command["value"] is not JsonValue answerValue)
        {
            return;
        }

        var answer = answerValue.GetValue<string>();
        await File.WriteAllTextAsync(
            Path.Combine(_arguments.SessionDirectory, "question-response.txt"),
            answer,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        var prompt = _pendingInteractionPrompt;
        _pendingInteractionPrompt = null;
        await EmitInteractionCompletionAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitInteractionCompletionAsync(
        string prompt,
        string selectedAnswer,
        CancellationToken cancellationToken)
    {
        var answer = $"Approved. Selected: {selectedAnswer}.";
        await _session.AppendTurnAsync(prompt, answer, cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = FakeSessionStore.AssistantMessage(string.Empty),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_start", 0, null, cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_delta", 0, answer, cancellationToken).ConfigureAwait(false);
        await WriteMessageDeltaAsync("text_end", 0, answer, cancellationToken).ConfigureAwait(false);
        var finalMessage = FakeSessionStore.AssistantMessage(answer);
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "message_end",
            ["message"] = finalMessage.DeepClone(),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        await WriteSettlementAsync(finalMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueOutOfOrderAsync(string id, string command, CancellationToken cancellationToken)
    {
        _outOfOrderRequests.Add((id, command));
        if (_outOfOrderRequests.Count < 2)
        {
            return;
        }

        foreach (var request in _outOfOrderRequests.AsEnumerable().Reverse())
        {
            if (request.Command == "get_last_assistant_text")
            {
                await _writer.WriteAsync(Response(request.Id, request.Command, new JsonObject { ["text"] = "latest" }),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (entries, leafId) = await _session.ReadAsync(cancellationToken).ConfigureAwait(false);
                await _writer.WriteAsync(Response(request.Id, request.Command, new JsonObject
                {
                    ["entries"] = entries,
                    ["leafId"] = leafId,
                }), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        _outOfOrderRequests.Clear();
    }

    private async Task WriteStateAsync(string id, CancellationToken cancellationToken)
    {
        var (entries, _) = await _session.ReadAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WriteAsync(Response(id, "get_state", new JsonObject
        {
            ["model"] = Model(
                _modelProvider,
                _modelId,
                _modelId == "fake-standard" ? "Fake Standard" : "Fake Fast",
                _modelId == "fake-standard"),
            ["thinkingLevel"] = _thinkingLevel,
            ["isStreaming"] = _isStreaming,
            ["isCompacting"] = false,
            ["steeringMode"] = _steeringMode,
            ["followUpMode"] = _followUpMode,
            ["sessionFile"] = _session.SessionFile,
            ["sessionId"] = _arguments.SessionId,
            ["sessionName"] = _sessionName,
            ["autoCompactionEnabled"] = _arguments.Scenario == "automation-unreported" ? null : JsonValue.Create(_autoCompactionEnabled),
            ["messageCount"] = entries.Count,
            ["pendingMessageCount"] = _queuedSteering.Count + _queuedFollowUp.Count,
        }), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Task WriteQueueUpdateAsync(CancellationToken cancellationToken) =>
        _writer.WriteAsync(new JsonObject
        {
            ["type"] = "queue_update",
            ["steering"] = new JsonArray(_queuedSteering
                .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["followUp"] = new JsonArray(_queuedFollowUp
                .Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        }, cancellationToken: cancellationToken);

    private string[] AvailableThinkingLevels() => _modelId == "fake-standard"
        ? ["off", "minimal", "low", "medium", "high"]
        : ["off"];

    private async Task WriteEntriesAsync(string id, string? since, CancellationToken cancellationToken)
    {
        var (allEntries, leafId) = await _session.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (since is null)
        {
            await _writer.WriteAsync(Response(id, "get_entries", new JsonObject
            {
                ["entries"] = allEntries,
                ["leafId"] = leafId,
            }), cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var index = -1;
        for (var candidate = 0; candidate < allEntries.Count; candidate++)
        {
            if (allEntries[candidate]?["id"]?.GetValue<string>() == since)
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            await _writer.WriteAsync(Error(id, "get_entries", $"Entry not found: {since}"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        var entries = new JsonArray();
        for (var candidate = index + 1; candidate < allEntries.Count; candidate++)
        {
            entries.Add(allEntries[candidate]?.DeepClone());
        }

        await _writer.WriteAsync(Response(id, "get_entries", new JsonObject
        {
            ["entries"] = entries,
            ["leafId"] = leafId,
        }), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryEmitFramingFailureAsync(CancellationToken cancellationToken)
    {
        switch (_arguments.Scenario)
        {
            case "parse-error":
                await _writer.WriteAsync(new JsonObject
                {
                    ["type"] = "response",
                    ["command"] = "parse",
                    ["success"] = false,
                    ["error"] = "synthetic parse failure",
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return true;
            case "malformed-json":
                await _writer.WriteRawAsync("{broken\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                _exitCode = 24;
                _stop.Cancel();
                return true;
            case "invalid-utf8":
                await _writer.WriteRawAsync([0xC3, 0x28, (byte)'\n'], cancellationToken).ConfigureAwait(false);
                _exitCode = 25;
                _stop.Cancel();
                return true;
            case "unterminated-jsonl":
                await _writer.WriteAsync(Response("ignored", "get_state"), terminate: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _exitCode = 26;
                _stop.Cancel();
                return true;
            default:
                return false;
        }
    }

    private async Task WriteMessageDeltaAsync(
        string type,
        int contentIndex,
        string? value,
        CancellationToken cancellationToken,
        bool split = false)
    {
        var delta = new JsonObject { ["type"] = type, ["contentIndex"] = contentIndex };
        if (type.EndsWith("_delta", StringComparison.Ordinal))
        {
            delta["delta"] = value;
        }
        else if (type.EndsWith("_end", StringComparison.Ordinal))
        {
            delta["content"] = value;
        }

        await _writer.WriteAsync(MessageUpdate(delta), split, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteToolUpdateAsync(string output, CancellationToken cancellationToken)
    {
        await _writer.WriteAsync(new JsonObject
        {
            ["type"] = "tool_execution_update",
            ["toolCallId"] = "call-1",
            ["toolName"] = "bash",
            ["args"] = new JsonObject { ["command"] = "echo hi" },
            ["partialResult"] = ToolResult(output),
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendCommandLogAsync(
        string command,
        JsonObject request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_arguments.SessionDirectory);
        var record = new JsonObject
        {
            ["command"] = command,
            ["request"] = request.DeepClone(),
        };
        if (command == "prompt")
        {
            record["message"] = request["message"]?.DeepClone();
            record["images"] = request["images"]?.DeepClone();
        }

        var line = record.ToJsonString() + "\n";
        await File.AppendAllTextAsync(_commandLog, line, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject MessageUpdate(JsonObject delta) => new()
    {
        ["type"] = "message_update",
        ["usage"] = FakeSessionStore.Usage(),
        ["assistantMessageEvent"] = delta,
    };

    private static JsonObject ToolCall(
        string id = "call-1",
        string name = "bash",
        string argumentName = "command",
        string argumentValue = "echo hi") => new()
    {
        ["type"] = "toolCall",
        ["id"] = id,
        ["name"] = name,
        ["arguments"] = new JsonObject { [argumentName] = argumentValue },
    };

    private static JsonObject ToolResult(string output) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = output }),
        ["details"] = new JsonObject { ["truncation"] = null, ["fullOutputPath"] = null },
    };

    private static JsonObject Model(
        string provider,
        string modelId,
        string displayName,
        bool supportsReasoning) => new()
    {
        ["id"] = modelId,
        ["name"] = displayName,
        ["api"] = "fake-api",
        ["provider"] = provider,
        ["baseUrl"] = "https://fake.invalid",
        ["reasoning"] = supportsReasoning,
        ["input"] = new JsonArray("text"),
        ["contextWindow"] = 100_000,
        ["maxTokens"] = 8_192,
        ["cost"] = new JsonObject
        {
            ["input"] = 0,
            ["output"] = 0,
            ["cacheRead"] = 0,
            ["cacheWrite"] = 0,
        },
    };

    private static JsonObject Response(string id, string command, JsonObject? data = null)
    {
        var response = new JsonObject
        {
            ["id"] = id,
            ["type"] = "response",
            ["command"] = command,
            ["success"] = true,
        };
        if (data is not null)
        {
            response["data"] = data;
        }

        return response;
    }

    private static JsonObject Error(string? id, string command, string error)
    {
        var response = new JsonObject
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = false,
            ["error"] = error,
        };
        if (id is not null)
        {
            response["id"] = id;
        }

        return response;
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stop.Dispose();
    }
}
