namespace PiStation.App.Composition;

/// <summary>Ctrl+Q policy. Holding completes on release so repeated Q input cannot reach another app.</summary>
internal sealed class QuitGesture
{
    internal const int HoldMilliseconds = 1200;
    internal const int DoublePressMilliseconds = 500;
    private long? _pressedAt;
    private long? _firstPressAt;
    private int _mode;
    public bool IsHolding => _mode == 1 && _pressedAt.HasValue;
    public bool IsPending => _pressedAt.HasValue || _firstPressAt.HasValue;

    public bool KeyDown(int mode, long now, bool isRepeat)
    {
        if (mode != _mode) Reset();
        _mode = mode;
        if (isRepeat || _pressedAt.HasValue) return false;
        _pressedAt = now;
        if (mode == 0) { Reset(); return true; }
        if (mode == 2)
        {
            if (_firstPressAt is { } first && now - first is >= 0 and <= DoublePressMilliseconds)
            {
                Reset();
                return true;
            }
            _firstPressAt = now;
        }
        return false;
    }

    public bool KeyUp(long now)
    {
        var completed = IsHolding && now - _pressedAt!.Value >= HoldMilliseconds;
        _pressedAt = null;
        if (_mode != 2) Reset();
        return completed;
    }

    public string Hint(long now) => IsHolding
        ? now - _pressedAt!.Value >= HoldMilliseconds
            ? "Release Q to close this window."
            : "Keep holding Ctrl+Q to close this window."
        : "Press Ctrl+Q again to close this window.";

    public void Expire(long now)
    {
        if (_mode == 2 && _firstPressAt is { } first && now - first > DoublePressMilliseconds) Reset();
    }

    public void Reset() => _pressedAt = _firstPressAt = null;
}
