using System.Text;
using System.Text.RegularExpressions;

namespace PiStation.Host.Terminals;

// Retain small chunks so trimming/append never copies the entire transcript.
internal sealed class TerminalHistory(int characterLimit, int lineLimit = 5000, int byteLimit = 8 * 1024 * 1024)
{
    private readonly LinkedList<string> _chunks = [];
    private int _characters, _bytes, _newlines;
    private char _last;
    private string? _cached = "";

    public void Append(string text)
    {
        if (text.Length == 0) return;
        _cached = null;
        // Join a surrogate pair split by a transport read before measuring UTF-8.
        if (_chunks.Last is { } previous && char.IsHighSurrogate(_last) && char.IsLowSurrogate(text[0]))
        {
            previous.Value += text[0];
            _characters++; _bytes++; _last = text[0];
            text = text[1..];
        }
        for (var offset = 0; offset < text.Length;)
        {
            var count = Math.Min(4096, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
            var chunk = text.Substring(offset, count);
            if (_chunks.Last is { } last && last.Value.Length + chunk.Length <= 4096)
                last.Value += chunk;
            else
                _chunks.AddLast(chunk);
            _characters += chunk.Length; _bytes += Encoding.UTF8.GetByteCount(chunk); _newlines += chunk.Count(c => c == '\n');
            _last = chunk[^1]; offset += count;
            Trim();
        }
        Trim();
    }

    private void Trim()
    {
        while (_chunks.First is { } first && (_characters > characterLimit || _bytes > byteLimit || _newlines + (_last == '\n' ? 0 : 1) > lineLimit))
        {
            var text = first.Value;
            var count = 0;
            var bytes = 0;
            var lines = 0;
            while (count < text.Length && (_characters - count > characterLimit || _bytes - bytes > byteLimit ||
                _newlines - lines + (_last == '\n' ? 0 : 1) > lineLimit))
            {
                var length = char.IsHighSurrogate(text[count]) && count + 1 < text.Length && char.IsLowSurrogate(text[count + 1]) ? 2 : 1;
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(count, length));
                if (text[count] == '\n') lines++;
                count += length;
            }
            _characters -= count; _bytes -= bytes; _newlines -= lines;
            if (count == text.Length) _chunks.RemoveFirst(); else first.Value = text[count..];
        }
    }

    public void Clear() { _chunks.Clear(); _characters = _bytes = _newlines = 0; _last = default; _cached = ""; }
    public override string ToString() => _cached ??= string.Concat(_chunks);
}

// Keep visual ANSI sequences, but not queries/replies that would be executed again
// during hydration. Control sequences may span arbitrary PTY output chunks.
internal sealed class TerminalHistorySanitizer
{
    private readonly StringBuilder _pending = new();
    private char _kind;
    private bool _discard;
    private char _previous;

    public void Clear() { _pending.Clear(); _kind = default; _discard = false; _previous = default; }

    public string Append(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (_kind == default)
            {
                if (character is '\x1b' or '\x9b' or '\x9d' or '\x90')
                {
                    _kind = character switch { '\x9b' => '[', '\x9d' => ']', '\x90' => 'P', _ => '\x1b' };
                    _pending.Append(character); _previous = character;
                }
                else result.Append(character);
                continue;
            }
            if (!_discard) _pending.Append(character);
            if (_pending.Length > 8192) { _pending.Clear(); _discard = true; }
            if (_kind == '\x1b' && character is '[' or ']' or 'P' or '_' or '^' or 'X')
            { _kind = character; _previous = character; continue; }
            var complete = _kind switch
            {
                '[' => character is >= '\x40' and <= '\x7e',
                ']' or 'P' or '_' or '^' or 'X' => character == '\x9c' || (_previous == '\x1b' && character == '\\') || (_kind == ']' && character == '\a'),
                _ => character is >= '\x30' and <= '\x7e',
            };
            if (complete)
            {
                var sequence = _pending.ToString();
                if (!_discard && !ShouldStrip(sequence, _kind)) result.Append(sequence);
                Clear();
            }
            else _previous = character;
        }
        return result.ToString();
    }

    private static bool ShouldStrip(string sequence, char kind)
    {
        var body = sequence[(sequence[0] == '\x1b' ? 2 : 1)..];
        if (kind == '[')
        {
            var final = body[^1]; body = body[..^1];
            return final == 'n' || (final is 'R' or 'c' && Regex.IsMatch(body, "^[>0-9;?]*$")) ||
                (final is 'p' or 'y' && Regex.IsMatch(body, "^[0-9;?]*\\$$")) ||
                (final == 'q' && body.StartsWith('>')) || (final == 'u' && body.StartsWith('?'));
        }
        return kind == 'P' && Regex.IsMatch(body, "^[01]?[$+][qr]") ||
            kind == ']' && Regex.IsMatch(body, "^(10|11|12);(\\?|rgb:)");
    }
}
