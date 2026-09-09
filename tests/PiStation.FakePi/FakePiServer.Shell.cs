using System.Text.Json.Nodes;

namespace PiStation.FakePi;

internal sealed partial class FakePiServer
{
    private TaskCompletionSource? _bashCancelled;

    private async Task HandleBashAsync(string id, JsonObject request, CancellationToken token)
    {
        try
        {
            var command = request["command"]!.GetValue<string>();
            if (command == "reject")
            {
                await _writer.WriteAsync(Error(id, "bash", "Shell unavailable"), cancellationToken: token).ConfigureAwait(false);
                return;
            }
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bashCancelled = cancellation;
            var output = "started 😀\n";
            // An unrelated update must never leak into the command's output.
            await _writer.WriteAsync(new JsonObject { ["type"] = "bash_execution_update", ["id"] = "unknown-id", ["delta"] = "wrong-command" }, cancellationToken: token).ConfigureAwait(false);
            await _writer.WriteAsync(new JsonObject { ["type"] = "bash_execution_update", ["id"] = id, ["delta"] = output }, cancellationToken: token).ConfigureAwait(false);
            if (command == "wait") await cancellation.Task.WaitAsync(token).ConfigureAwait(false);
            if (command == "crash") { Environment.Exit(43); return; }
            if (command == "large") output += new string('x', 70000) + "\nlast line";
            var cancelled = cancellation.Task.IsCompleted;
            var result = new JsonObject
            {
                ["output"] = output, ["cancelled"] = cancelled, ["truncated"] = command == "large",
                ["exitCode"] = cancelled ? null : command == "fail" ? 7 : 0,
                ["fullOutputPath"] = command == "large" ? Path.Combine(Environment.CurrentDirectory, "shell-output.txt") : null,
            };
            var message = (JsonObject)result.DeepClone();
            message["role"] = "bashExecution";
            message["command"] = command;
            message["excludeFromContext"] = request["excludeFromContext"]?.DeepClone();
            await _session.AppendShellAsync(message, token).ConfigureAwait(false);
            await _writer.WriteAsync(Response(id, "bash", result), cancellationToken: token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _error.WriteLineAsync(error.ToString()).ConfigureAwait(false);
            _stop.Cancel();
        }
    }
}
