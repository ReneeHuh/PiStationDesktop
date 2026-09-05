using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiStation.PiRpc.Decoding;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Wire;
using PiStation.PiRpc.Wire.Events;
using PiStation.PiRpc.Wire.Responses;

namespace PiStation.PiRpc.Transport;

public sealed class PiRpcConnection : IAsyncDisposable
{
    private static readonly HashSet<string> NativeImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    private static readonly HashSet<string> DialogMethods = new(StringComparer.Ordinal)
    {
        "select",
        "confirm",
        "input",
        "editor",
    };

    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Channel<PiRpcEvent> _events = Channel.CreateUnbounded<PiRpcEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly object _lifecycleLock = new();
    private readonly PiRpcConnectionOptions _options;
    private readonly Stream _outputFromPi;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly JsonlRecordReader _reader;
    private readonly JsonlRecordWriter _writer;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readTask;
    private Exception? _terminalException;
    private long _requestId;
    private bool _disposed;

    public PiRpcConnection(
        Stream inputToPi,
        Stream outputFromPi,
        PiRpcConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputToPi);
        ArgumentNullException.ThrowIfNull(outputFromPi);

        _options = options ?? new PiRpcConnectionOptions();
        _outputFromPi = outputFromPi;
        _reader = new JsonlRecordReader(_options.MaximumRecordBytes);
        _writer = new JsonlRecordWriter(inputToPi, leaveOpen: true);
    }

    public Task Completion => _completion.Task;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _readTask ??= RunReadLoopAsync();
        }
    }

    public async IAsyncEnumerable<PiRpcEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var @event in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return @event;
        }
    }

    public Task PromptAsync(string message, CancellationToken cancellationToken = default) =>
        PromptAsync(message, [], cancellationToken);

    public async Task PromptAsync(
        string message,
        IReadOnlyList<PiPromptAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attachments);
        if (string.IsNullOrWhiteSpace(message) && attachments.Count == 0)
        {
            throw new ArgumentException("A prompt must contain text or at least one attachment.", nameof(message));
        }

        var command = new JsonObject
        {
            ["message"] = PiPromptFormatter.CreateRpcMessage(message, attachments),
        };
        var images = new JsonArray();
        foreach (var attachment in attachments.Where(
                     attachment => NativeImageMediaTypes.Contains(attachment.MediaType)))
        {
            var file = new FileInfo(attachment.ServerPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    $"Prompt attachment '{attachment.FileName}' was not found.",
                    attachment.ServerPath);
            }

            if (file.Length > _options.MaximumPromptImageBytes)
            {
                throw new InvalidDataException(
                    $"Prompt image '{attachment.FileName}' exceeds the Pi RPC image limit.");
            }

            var bytes = await File.ReadAllBytesAsync(attachment.ServerPath, cancellationToken).ConfigureAwait(false);
            images.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(bytes),
                ["mimeType"] = attachment.MediaType,
            });
        }

        if (images.Count != 0)
        {
            command["images"] = images;
        }

        var response = await SendCommandAsync(
            "prompt",
            command,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<PiClearedMessages> ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("clear_queue", cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);

        var data = GetRequiredData(response);
        return new PiClearedMessages(
            ReadStringArray(data, "steering"),
            ReadStringArray(data, "followUp"));
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("abort", cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task AbortRetryAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("abort_retry", cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<PiClearedMessages> StopAsync(CancellationToken cancellationToken = default)
    {
        var cleared = await ClearQueueAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AbortAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PiRpcTimeoutException)
        {
            await AbortRetryAsync(cancellationToken).ConfigureAwait(false);
            await AbortAsync(cancellationToken).ConfigureAwait(false);
        }

        return cleared;
    }

    public async Task<PiSessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get_state", cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var data = GetRequiredData(response);

        return new PiSessionState(
            GetRequiredString(data, "sessionId"),
            GetOptionalString(data, "sessionFile"),
            ReadOptionalModel(data, "model"),
            GetRequiredString(data, "thinkingLevel"),
            GetBoolean(data, "isStreaming"),
            GetBoolean(data, "isCompacting"),
            GetInt32(data, "messageCount"),
            GetInt32(data, "pendingMessageCount"));
    }

    public async Task<IReadOnlyList<PiModelInfo>> GetAvailableModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get_available_models", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        var data = GetRequiredData(response);
        if (!data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new PiRpcConnectionException("Pi get_available_models response did not contain a models array.");
        }

        return models.EnumerateArray().Select(ReadModel).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetAvailableThinkingLevelsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
            "get_available_thinking_levels",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return ReadStringArray(GetRequiredData(response), "levels");
    }

    public async Task<PiModelInfo> SetModelAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var response = await SendCommandAsync(
            "set_model",
            new JsonObject
            {
                ["provider"] = providerId,
                ["modelId"] = modelId,
            },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return ReadModel(GetRequiredData(response));
    }

    public async Task SetThinkingLevelAsync(
        string level,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        var response = await SendCommandAsync(
            "set_thinking_level",
            new JsonObject { ["level"] = level },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<PiSessionEntries> GetEntriesAsync(
        string? since = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new JsonObject();
        if (since is not null)
        {
            arguments["since"] = since;
        }

        var response = await SendCommandAsync("get_entries", arguments, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var data = GetRequiredData(response);
        if (!data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new PiRpcConnectionException("Pi get_entries response did not contain an entries array.");
        }

        return new PiSessionEntries(
            entries.EnumerateArray().Select(static entry => entry.Clone()).ToArray(),
            GetOptionalString(data, "leafId"));
    }

    public async Task<PiSessionMutation> ForkAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var response = await SendCommandAsync(
            "fork",
            new JsonObject { ["entryId"] = entryId },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return new PiSessionMutation(GetBoolean(GetRequiredData(response), "cancelled"));
    }

    public async Task<PiSessionMutation> NewSessionAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("new_session", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return new PiSessionMutation(GetBoolean(GetRequiredData(response), "cancelled"));
    }

    public Task RespondToExtensionConfirmAsync(
        string requestId,
        bool confirmed,
        CancellationToken cancellationToken = default) => SendExtensionUiResponseAsync(
            requestId,
            new JsonObject { ["confirmed"] = confirmed },
            cancellationToken);

    public Task RespondToExtensionTextAsync(
        string requestId,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SendExtensionUiResponseAsync(
            requestId,
            new JsonObject { ["value"] = value },
            cancellationToken);
    }

    public Task CancelExtensionUiAsync(
        string requestId,
        CancellationToken cancellationToken = default) => SendExtensionUiResponseAsync(
            requestId,
            new JsonObject { ["cancelled"] = true },
            cancellationToken);

    public async Task<PiRpcResponse> SendCommandAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        EnsureRunning();

        var terminalException = Volatile.Read(ref _terminalException);
        if (terminalException is not null)
        {
            throw new PiRpcConnectionException("The Pi RPC connection is faulted.", terminalException);
        }

        var id = $"req_{Interlocked.Increment(ref _requestId)}";
        var request = new JsonObject
        {
            ["id"] = id,
            ["type"] = command,
        };

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                if (argument.Key is "id" or "type")
                {
                    throw new ArgumentException($"Command argument '{argument.Key}' is reserved.", nameof(arguments));
                }

                request[argument.Key] = argument.Value?.DeepClone();
            }
        }

        var pending = new PendingRequest(command);
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException($"Duplicate Pi request id '{id}'.");
        }

        try
        {
            await _writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(new PiRpcConnectionException($"Failed to write Pi command '{command}'.", exception));
            throw;
        }

        var timeout = GetTimeout(command);
        try
        {
            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new PiRpcTimeoutException(command, timeout);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void ReportProcessExit(int? exitCode, string? stderr)
    {
        var suffix = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $" Stderr: {stderr}";
        Fault(new PiRpcConnectionException(
            $"Pi process exited with code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.{suffix}"));
    }

    public async ValueTask DisposeAsync()
    {
        Task? readTask;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();
            readTask = _readTask;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);

        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                _options.DiagnosticSink?.Invoke("Pi RPC reader did not finish before connection disposal.");
            }
        }

        CompleteCleanly();
        _disposeCancellation.Dispose();
    }

    private async Task RunReadLoopAsync()
    {
        try
        {
            await _reader.ReadAsync(_outputFromPi, HandleRecordAsync, _disposeCancellation.Token).ConfigureAwait(false);
            if (!_disposeCancellation.IsCancellationRequested)
            {
                Fault(new PiRpcConnectionException("Pi stdout closed unexpectedly."));
            }
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            CompleteCleanly();
        }
        catch (Exception exception)
        {
            Fault(exception is PiRpcConnectionException
                ? exception
                : new PiRpcConnectionException("Pi RPC read loop failed.", exception));
        }
    }

    private async ValueTask HandleRecordAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new PiRpcConnectionException("Pi stdout contained malformed JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty) || typeProperty.ValueKind != JsonValueKind.String)
            {
                throw new PiRpcConnectionException("Pi stdout record did not contain a string type discriminator.");
            }

            var type = typeProperty.GetString();
            if (type == "response")
            {
                HandleResponse(root);
                return;
            }

            if (type == "extension_ui_request")
            {
                await HandleExtensionUiRequestAsync(root, cancellationToken).ConfigureAwait(false);
                return;
            }

            var @event = PiEventDecoder.Decode(root);
            await _events.Writer.WriteAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleResponse(JsonElement record)
    {
        var response = record.Deserialize(PiJsonContext.Default.PiRpcResponse)
            ?? throw new PiRpcConnectionException("Pi response could not be decoded.");

        if (string.IsNullOrWhiteSpace(response.Id))
        {
            if (response.Command == "parse")
            {
                throw new PiRpcConnectionException(
                    $"Pi rejected malformed client JSON: {response.Error ?? "unknown parse error"}");
            }

            throw new PiRpcConnectionException($"Pi response for '{response.Command}' did not contain a correlation id.");
        }

        if (_pending.TryRemove(response.Id, out var pending))
        {
            pending.Completion.TrySetResult(response);
            return;
        }

        _options.DiagnosticSink?.Invoke($"Ignored Pi response with unknown id '{response.Id}'.");
    }

    private async ValueTask HandleExtensionUiRequestAsync(JsonElement record, CancellationToken cancellationToken)
    {
        var id = GetRequiredString(record, "id");
        var method = GetRequiredString(record, "method");
        if (!DialogMethods.Contains(method))
        {
            _options.DiagnosticSink?.Invoke($"Ignored fire-and-forget Pi extension UI method '{method}'.");
            return;
        }

        var @event = PiEventDecoder.Decode(record);
        await _events.Writer.WriteAsync(@event, cancellationToken).ConfigureAwait(false);
        _options.DiagnosticSink?.Invoke($"Forwarded blocking Pi extension UI method '{method}' with id '{id}'.");
    }

    private async Task SendExtensionUiResponseAsync(
        string requestId,
        JsonObject values,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        EnsureRunning();

        var terminalException = Volatile.Read(ref _terminalException);
        if (terminalException is not null)
        {
            throw new PiRpcConnectionException("The Pi RPC connection is faulted.", terminalException);
        }

        var response = new JsonObject
        {
            ["type"] = "extension_ui_response",
            ["id"] = requestId,
        };
        foreach (var value in values)
        {
            response[value.Key] = value.Value?.DeepClone();
        }

        try
        {
            await _writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fault(new PiRpcConnectionException(
                $"Failed to write Pi extension UI response '{requestId}'.",
                exception));
            throw;
        }
    }

    private void EnsureRunning()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readTask is null)
            {
                throw new InvalidOperationException("Start the Pi RPC connection before sending commands.");
            }
        }
    }

    private TimeSpan GetTimeout(string command) => command is "prompt" or "compact"
        ? _options.LongRunningCommandTimeout
        : _options.DefaultCommandTimeout;

    private void Fault(Exception exception)
    {
        lock (_lifecycleLock)
        {
            if (_terminalException is not null || _disposed)
            {
                return;
            }

            _terminalException = exception;
        }

        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetException(exception);
        }

        _pending.Clear();
        _events.Writer.TryComplete(exception);
        _completion.TrySetException(exception);
    }

    private void CompleteCleanly()
    {
        _events.Writer.TryComplete();
        _completion.TrySetResult();
    }

    private static void EnsureSuccess(PiRpcResponse response)
    {
        if (!response.Success)
        {
            throw new PiRpcCommandException(response.Command, response.Error ?? "unknown error");
        }
    }

    private static JsonElement GetRequiredData(PiRpcResponse response) =>
        response.Data.ValueKind == JsonValueKind.Undefined
            ? throw new PiRpcConnectionException($"Pi response for '{response.Command}' did not contain data.")
            : response.Data;

    private static string GetRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new PiRpcConnectionException($"Pi JSON property '{name}' must be a string.");
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PiModelInfo? ReadOptionalModel(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadModel(value);
    }

    private static PiModelInfo ReadModel(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object)
        {
            throw new PiRpcConnectionException("Pi model data must be an object.");
        }

        var modelId = GetRequiredString(model, "id");
        return new PiModelInfo(
            GetRequiredString(model, "provider"),
            modelId,
            GetOptionalString(model, "name") ?? modelId,
            GetBoolean(model, "reasoning"),
            GetOptionalPositiveInt32(model, "contextWindow"));
    }

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static int GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static int? GetOptionalPositiveInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result) &&
        result > 0
            ? result
            : null;

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray();
    }

    private sealed class PendingRequest(string command)
    {
        public string Command { get; } = command;

        public TaskCompletionSource<PiRpcResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
