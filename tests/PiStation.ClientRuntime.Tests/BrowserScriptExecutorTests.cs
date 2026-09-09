using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserScriptExecutorTests
{
    private const string Success = "{\"result\":{\"type\":\"number\",\"value\":42}}";
    private static readonly string[] CancellationCalls = ["Runtime.evaluate", "Runtime.terminateExecution", "Runtime.releaseObjectGroup", "Runtime.evaluate", "Runtime.releaseObjectGroup"];

    [Fact]
    public async Task CancellationDrainsTerminationBeforeNextEvaluationAndDiscardsLateResult()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var evaluations = 0;
        Task<string> Send(string method, string json)
        {
            calls.Add(method);
            if (method == "Runtime.evaluate")
            {
                using var parameters = JsonDocument.Parse(json);
                Assert.True(parameters.RootElement.GetProperty("returnByValue").GetBoolean());
                Assert.True(parameters.RootElement.GetProperty("awaitPromise").GetBoolean());
                Assert.Equal(5000, parameters.RootElement.GetProperty("timeout").GetInt32());
                return ++evaluations == 1 ? pending.Task : Task.FromResult(Success);
            }
            if (method == "Runtime.terminateExecution") { terminating.SetResult(); return terminated.Task; }
            return Task.FromResult("{}");
        }
        await using var executor = new BrowserScriptExecutor(Send, () => Assert.Fail("Responsive browser must remain open."));
        using var cancelled = new CancellationTokenSource();
        var first = executor.EvaluateAsync("new Promise(() => {})", true, 5000, () => { }, cancelled.Token);
        cancelled.Cancel();
        await terminating.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = executor.EvaluateAsync("42", true, 5000, () => { }, CancellationToken.None);
        Assert.Equal(1, evaluations);
        pending.SetResult(Success);
        terminated.SetResult("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(42, (await second).GetProperty("value").GetInt32());
        Assert.Equal(CancellationCalls, calls);
    }

    [Fact]
    public async Task TimeoutTerminatesExecutionAndFailedTerminationClosesAndBlocksTheBrowser()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var calls = 0;
        await using var executor = new BrowserScriptExecutor((method, _) =>
        {
            calls++;
            return method == "Runtime.evaluate" ? pending.Task : Task.FromException<string>(new InvalidOperationException("Debugger disconnected"));
        }, () => closed = true);
        await Assert.ThrowsAsync<TimeoutException>(() => executor.EvaluateAsync("while(true){}", true, 100, () => { }, CancellationToken.None));
        Assert.True(closed);
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.EvaluateAsync("42", true, 100, () => { }, CancellationToken.None));
        Assert.Equal(2, calls);
        pending.SetException(new InvalidOperationException("Browser closed"));
    }

    [Fact]
    public async Task RevokedPermissionNeverSubmitsScript()
    {
        await using var executor = new BrowserScriptExecutor((_, _) => throw new InvalidOperationException("Unexpected CDP call"), () => { });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.EvaluateAsync("42", true, 5000,
            () => throw new UnauthorizedAccessException(), CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"result\":{\"type\":\"bigint\",\"unserializableValue\":\"42n\"}}")]
    [InlineData("{\"result\":{\"type\":\"number\",\"unserializableValue\":\"NaN\"}}")]
    [InlineData("{\"result\":{\"type\":\"object\",\"objectId\":\"remote\"}}")]
    [InlineData("{\"result\":{\"type\":\"function\",\"value\":{}}}")]
    [InlineData("{\"result\":{\"type\":\"object\",\"subtype\":\"promise\",\"value\":{}}}")]
    [InlineData("{\"result\":{\"type\":\"object\",\"subtype\":\"node\",\"value\":{}}}")]
    public void UnsupportedResultsAreExplicitErrors(string raw) => Assert.Throws<InvalidOperationException>(() => BrowserScriptExecutor.DecodeResult(raw));

    [Fact]
    public void ResultAndExceptionLimitsIncludeUtf8AndUndefinedIsExplicit()
    {
        var undefined = BrowserScriptExecutor.DecodeResult("{\"result\":{\"type\":\"undefined\"}}");
        Assert.Equal("undefined", undefined.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, undefined.GetProperty("value").ValueKind);
        Assert.Equal(42, BrowserScriptExecutor.DecodeResult(Success).GetProperty("value").GetInt32());
        var raw = JsonSerializer.Serialize(new { result = new { type = "string", value = new string('界', 22000) } });
        Assert.Throws<InvalidOperationException>(() => BrowserScriptExecutor.DecodeResult(raw));
        var error = JsonSerializer.Serialize(new { exceptionDetails = new { exception = new { description = new string('e', 10000) } } });
        Assert.True(Assert.Throws<InvalidOperationException>(() => BrowserScriptExecutor.DecodeResult(error)).Message.Length < 1100);
        var accepted = BrowserScriptExecutor.DecodeResult(JsonSerializer.Serialize(new { result = new { type = "string", value = new string('x', 1000) } }));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(accepted.GetRawText()) <= BrowserAutomationLimits.MaximumEvaluationBytes);
    }

    [Fact]
    public async Task NativeProtocolErrorsExplainRecoveryAndReleaseObjects()
    {
        var released = false;
        await using var executor = new BrowserScriptExecutor((method, _) =>
        {
            if (method == "Runtime.evaluate") return Task.FromException<string>(new ArgumentException("The parameter is incorrect."));
            released = method == "Runtime.releaseObjectGroup";
            return Task.FromResult("{}");
        }, () => Assert.Fail("A completed protocol failure should not close the browser."));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.EvaluateAsync("cyclic", true, 100, () => { }, CancellationToken.None));
        Assert.Contains("cyclic", error.Message);
        Assert.True(released);
    }
}
