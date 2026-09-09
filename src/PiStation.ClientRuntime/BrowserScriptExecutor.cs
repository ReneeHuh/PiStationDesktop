using System.Text;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

/// <summary>Serializes evaluations and drains termination before another script can start.</summary>
public sealed class BrowserScriptExecutor(Func<string, string, Task<string>> send, Action stopUnresponsiveBrowser) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _unresponsive;
    private bool _disposed;

    public async Task<JsonElement> EvaluateAsync(string expression, bool awaitPromise, int timeoutMs,
        Action validate, CancellationToken cancellationToken, int maximumBytes = BrowserAutomationLimits.MaximumEvaluationBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_unresponsive) throw new InvalidOperationException("The browser could not stop its previous script. Close and reopen this tab.");
            validate();
            cancellationToken.ThrowIfCancellationRequested();
            var group = "pistation-" + Guid.NewGuid().ToString("N");
            var pending = send("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = CheckedExpression(expression, awaitPromise), awaitPromise, returnByValue = true, userGesture = false,
                timeout = timeoutMs, disableBreaks = true, objectGroup = group,
            }));
            try
            {
                var raw = await pending.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
                validate();
                cancellationToken.ThrowIfCancellationRequested();
                return DecodeResult(raw, maximumBytes);
            }
            catch (Exception) when (!pending.IsCompleted)
            {
                // Cancellation of a .NET wait does not cancel Chromium. Do not leave a late
                // termination command able to interrupt the next agent operation.
                try
                {
                    await send("Runtime.terminateExecution", "{}").WaitAsync(TimeSpan.FromSeconds(2));
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception) when (pending.IsCompleted) { }
                }
                catch
                {
                    _unresponsive = true;
                    stopUnresponsiveBrowser();
                }
                _ = ObserveAsync(pending);
                throw;
            }
            catch (ArgumentException error)
            {
                // WebView2 maps CDP errors such as cyclic serialization and engine
                // timeouts to E_INVALIDARG, losing Chromium's original description.
                throw new InvalidOperationException("Chromium could not evaluate or serialize this expression. Check for a script timeout, a cyclic result, or an unsupported browser value.", error);
            }
            finally
            {
                if (!_unresponsive)
                {
                    try { await send("Runtime.releaseObjectGroup", JsonSerializer.Serialize(new { objectGroup = group })).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception) { /* A destroyed/navigated context has already released its objects. */ }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; } catch (Exception) { }
    }

    private static string CheckedExpression(string expression, bool awaitPromise) => $$"""
        (() => {
          const check = value => {
            if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
              const prototype = Object.getPrototypeOf(value);
              if (prototype !== Object.prototype && prototype !== null)
                throw new TypeError('Return a plain JSON value. Await Promises; extract fields from DOM nodes and other browser objects.');
            }
            return value;
          };
          const value = (0, eval)({{JsonSerializer.Serialize(expression)}});
          return {{(awaitPromise ? "Promise.resolve(value).then(check)" : "check(value)")}};
        })()
        """;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // The owner first cancels its document/access token. Drain the in-flight
        // evaluation, including termination, before disposing its semaphore.
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
    }

    public static JsonElement DecodeResult(string raw, int maximumBytes = BrowserAutomationLimits.MaximumEvaluationBytes)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var exception))
        {
            var detail = exception.TryGetProperty("exception", out var thrown) && thrown.TryGetProperty("description", out var description)
                ? description.GetString() : exception.TryGetProperty("text", out var text) ? text.GetString() : "JavaScript failed.";
            throw new InvalidOperationException("JavaScript evaluation failed: " + BrowserDiagnostics.Limit(detail, 1024));
        }
        if (!root.TryGetProperty("result", out var result)) throw new InvalidOperationException("Chromium returned no evaluation result.");
        var type = result.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        if (type == "undefined") return JsonSerializer.SerializeToElement(new { type, value = (object?)null });
        if (result.TryGetProperty("unserializableValue", out _) || type is "function" or "symbol" or "bigint" ||
            result.TryGetProperty("subtype", out var subtype) && subtype.GetString() is not ("array" or "null") ||
            !result.TryGetProperty("value", out var value))
            throw new InvalidOperationException("The JavaScript result is not a JSON value. Return a string, number, boolean, null, array or plain object.");
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maximumBytes)
            throw new InvalidOperationException($"The JavaScript result exceeds {maximumBytes} UTF-8 bytes.");
        var encoded = JsonSerializer.SerializeToElement(new { type, value });
        if (Encoding.UTF8.GetByteCount(encoded.GetRawText()) > maximumBytes)
            throw new InvalidOperationException($"The JavaScript result exceeds {maximumBytes} UTF-8 bytes.");
        return encoded;
    }
}
