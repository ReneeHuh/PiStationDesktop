using System.Text.Json;
using PiStation.ClientRuntime;

namespace PiStation.App.Views.Controls;

public sealed partial class PreviewWebViewSurface
{
    public async Task<object> WaitForRenderedBrowserStateAsync(int timeoutMs, Action validate,
        Func<(double Width, double Height)>? expectedSize, string? colorScheme, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var stableSamples = 0;
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            validate();
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync("({width:innerWidth,height:innerHeight,dark:matchMedia('(prefers-color-scheme: dark)').matches,light:matchMedia('(prefers-color-scheme: light)').matches})")
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs - elapsed.ElapsedMilliseconds)), cancellationToken);
            validate();
            using var document = JsonDocument.Parse(raw);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The browser could not report its rendered state.");
            var width = value.GetProperty("width").GetInt32();
            var height = value.GetProperty("height").GetInt32();
            var dark = value.GetProperty("dark").GetBoolean();
            var light = value.GetProperty("light").GetBoolean();
            var expected = expectedSize?.Invoke();
            var matches = width > 0 && height > 0 &&
                (expected is null || Math.Abs(width - expected.Value.Width) <= 1 && Math.Abs(height - expected.Value.Height) <= 1) &&
                (colorScheme == "dark" ? dark : colorScheme == "light" ? light : dark || light);
            stableSamples = matches ? stableSamples + 1 : 0;
            if (stableSamples >= 2) return new { width, height, colorScheme = dark ? "dark" : "light" };
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException($"The browser did not render the requested state within {timeoutMs} ms.");
    }

    public async Task<object> PressAutomationKeyAsync(BrowserAutomationCommand command, Action validate)
    {
        await InitializeAsync();
        validate();
        if (command.Selector is { } selector)
        {
            var focused = await Browser.CoreWebView2.ExecuteScriptAsync($$"""
                (() => { const e = document.querySelector({{JsonSerializer.Serialize(selector)}});
                  if (!e || e.disabled) return false; e.focus(); return document.activeElement === e; })()
                """);
            validate();
            if (focused != "true") throw new InvalidOperationException("No focusable matching element.");
        }
        var key = command.Key!;
        var virtualKey = BrowserAutomationCommand.VirtualKey(key);
        var text = (command.Modifiers & ~8) == 0 ? key switch
        {
            "Enter" => "\r", "Space" => " ",
            _ when key.Length == 1 => (command.Modifiers & 8) != 0 ? key.ToUpperInvariant() : key,
            _ => "",
        } : "";
        var code = key.Length == 1 ? (char.IsAsciiDigit(key[0]) ? "Digit" + key : "Key" + key.ToUpperInvariant()) : key;
        try
        {
            await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type = "keyDown", key = key == "Space" ? " " : key, code,
                windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, modifiers = command.Modifiers, text,
            }));
        }
        finally
        {
            // Always release the key in the original WebView, even if permission changes.
            await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type = "keyUp", key = key == "Space" ? " " : key, code,
                windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, modifiers = command.Modifiers,
            }));
        }
        validate();
        return new { ok = true, key };
    }

    public async Task<object> ScrollAutomationAsync(BrowserAutomationCommand command, Action validate)
    {
        await InitializeAsync();
        validate();
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync($$"""
            (() => {
              const selector = {{JsonSerializer.Serialize(command.Selector)}};
              const e = selector ? document.querySelector(selector) : document.scrollingElement;
              if (!e) throw new Error('No matching scroll target');
              e.scrollBy({ left: {{command.DeltaX}}, top: {{command.DeltaY}}, behavior: 'instant' });
              return { ok: true, x: e.scrollLeft, y: e.scrollTop };
            })()
            """);
        validate();
        if (raw == "null") throw new InvalidOperationException("No matching scroll target or script failed.");
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    public async Task<object> WaitAutomationAsync(BrowserAutomationCommand command, Action validate, Func<bool> isNavigating)
    {
        await InitializeAsync();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var script = $$"""
            (() => {
              const selector = {{JsonSerializer.Serialize(command.Selector)}};
              const condition = {{JsonSerializer.Serialize(command.Condition)}};
              const value = {{JsonSerializer.Serialize(command.Value)}};
              if (condition === 'loaded') return document.readyState === 'complete';
              if (condition === 'url') return location.href.includes(value);
              const e = document.querySelector(selector);
              const visible = !!e && e.getClientRects().length > 0 &&
                  getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none';
              if (condition === 'hidden') return !visible;
              if (condition === 'text') return !!e && (e.textContent || '').includes(value);
              return visible;
            })()
            """;
        do
        {
            validate();
            if (isNavigating())
            {
                await Task.Delay(100);
                continue;
            }
            var result = await Browser.CoreWebView2.ExecuteScriptAsync(script).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, command.TimeoutMs - elapsed.ElapsedMilliseconds)));
            validate();
            if (result == "true") return new { ok = true, condition = command.Condition };
            if (result == "null") throw new InvalidOperationException("Wait condition could not be evaluated; check the selector.");
            await Task.Delay(100);
        } while (elapsed.ElapsedMilliseconds < command.TimeoutMs);
        throw new TimeoutException($"Browser wait timed out after {command.TimeoutMs} ms ({command.Condition}).");
    }
}
