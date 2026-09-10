using System.Globalization;
using System.Text.RegularExpressions;

namespace PiStation.ClientRuntime.Themes;

/// <summary>A literal CSS color converted to native sRGB. CSS alpha is last in hex input.</summary>
public readonly record struct ThemeColor(double R, double G, double B, double A = 1)
{
    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
    private static int Byte(double value) => (int)Math.Round(Clamp(value) * 255);
    public string Hex => $"#{Byte(R):x2}{Byte(G):x2}{Byte(B):x2}" + (A < 1 ? $"{Byte(A):x2}" : "");
    public ThemeColor Over(ThemeColor background) => new(R * A + background.R * (1 - A), G * A + background.G * (1 - A), B * A + background.B * (1 - A));
    public ThemeColor Mix(ThemeColor other, double amount) => new(R + (other.R - R) * amount, G + (other.G - G) * amount, B + (other.B - B) * amount);
    private static double Linear(double channel) => channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
    private static double Gamma(double channel) => Clamp(channel <= .0031308 ? 12.92 * channel : 1.055 * Math.Pow(channel, 1 / 2.4) - .055);
    public double Luminance => .2126 * Linear(R) + .7152 * Linear(G) + .0722 * Linear(B);
    public double Contrast(ThemeColor other) => (Math.Max(Luminance, other.Luminance) + .05) / (Math.Min(Luminance, other.Luminance) + .05);
    public static ThemeColor Parse(string value) => TryParse(value, out var color) ? color : throw new FormatException($"Invalid literal color '{value}'. Use hex, rgb, hsl, oklch or a CSS color name.");

    public static bool TryParse(string? value, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
        var text = value.Trim().ToLowerInvariant();
        try
        {
            if (Regex.IsMatch(text, "^#(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$"))
            {
                var hex = text[1..];
                if (hex.Length <= 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
                double Channel(int index) => int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
                color = new(Channel(0), Channel(2), Channel(4), hex.Length == 8 ? Channel(6) : 1);
                return true;
            }
            if (text == "transparent") { color = new(0, 0, 0, 0); return true; }
            if (text == "rebeccapurple") { color = new(102 / 255d, 51 / 255d, 153 / 255d); return true; }
            var named = System.Drawing.Color.FromName(text.Replace("grey", "gray", StringComparison.Ordinal));
            if (named.IsKnownColor && !named.IsSystemColor) { color = new(named.R / 255d, named.G / 255d, named.B / 255d, named.A / 255d); return true; }
            var match = Regex.Match(text, "^([a-z]+)\\(([^()]*)\\)$");
            if (!match.Success) return false;
            var function = match.Groups[1].Value;
            var body = match.Groups[2].Value;
            var space = "";
            if (function == "color")
            {
                var split = body.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length != 2) return false;
                space = split[0]; body = split[1];
            }
            string[] parts;
            if (body.Contains(','))
            {
                if (function is not ("rgb" or "rgba" or "hsl" or "hsla") || body.Contains('/')) return false;
                parts = body.Split(',').Select(part => part.Trim()).ToArray();
                if (parts.Length is < 3 or > 4) return false;
            }
            else
            {
                var alphaParts = body.Split('/');
                if (alphaParts.Length > 2) return false;
                parts = Regex.Split(alphaParts[0].Trim(), "\\s+");
                if (parts.Length != 3) return false;
                if (alphaParts.Length == 2) parts = [.. parts, alphaParts[1].Trim()];
            }
            if (parts.Any(string.IsNullOrWhiteSpace)) return false;
            static double Number(string part, double percentScale = 1)
            {
                var percent = part.EndsWith('%');
                var number = double.Parse(percent ? part[..^1] : part, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new FormatException();
                return percent ? number * percentScale / 100 : number;
            }
            static double Hue(string part)
            {
                if (part.EndsWith("grad", StringComparison.Ordinal)) return Number(part[..^4]) * .9;
                if (part.EndsWith("turn", StringComparison.Ordinal)) return Number(part[..^4]) * 360;
                if (part.EndsWith("rad", StringComparison.Ordinal)) return Number(part[..^3]) * 180 / Math.PI;
                return Number(part.EndsWith("deg", StringComparison.Ordinal) ? part[..^3] : part);
            }
            var alpha = parts.Length == 4 ? Clamp(Number(parts[3])) : 1;
            switch (function)
            {
                case "rgb": case "rgba":
                    color = new(Clamp(Number(parts[0], 255) / 255), Clamp(Number(parts[1], 255) / 255), Clamp(Number(parts[2], 255) / 255), alpha); break;
                case "hsl": case "hsla": case "hwb":
                    var hue = ((Hue(parts[0]) % 360) + 360) % 360 / 60;
                    var saturation = Clamp(Number(parts[1])); var lightness = Clamp(Number(parts[2]));
                    var chroma = function == "hwb" ? 1 : (1 - Math.Abs(2 * lightness - 1)) * saturation;
                    var x = chroma * (1 - Math.Abs(hue % 2 - 1));
                    var rgb = hue switch { < 1 => new ThemeColor(chroma, x, 0), < 2 => new(x, chroma, 0), < 3 => new(0, chroma, x), < 4 => new(0, x, chroma), < 5 => new(x, 0, chroma), _ => new(chroma, 0, x) };
                    if (function == "hwb")
                    {
                        if (saturation + lightness >= 1) color = new(saturation / (saturation + lightness), saturation / (saturation + lightness), saturation / (saturation + lightness), alpha);
                        else color = new(rgb.R * (1 - saturation - lightness) + saturation, rgb.G * (1 - saturation - lightness) + saturation, rgb.B * (1 - saturation - lightness) + saturation, alpha);
                    }
                    else { var m = lightness - chroma / 2; color = new(rgb.R + m, rgb.G + m, rgb.B + m, alpha); }
                    break;
                case "oklch": case "oklab":
                    var l = Clamp(Number(parts[0])); var a = Number(parts[1], .4); var b = function == "oklch" ? Hue(parts[2]) : Number(parts[2], .4);
                    if (function == "oklch") { var angle = b * Math.PI / 180; b = Math.Max(0, a) * Math.Sin(angle); a = Math.Max(0, a) * Math.Cos(angle); }
                    var ll = Math.Pow(l + .3963377774 * a + .2158037573 * b, 3);
                    var mm = Math.Pow(l - .1055613458 * a - .0638541728 * b, 3);
                    var ss = Math.Pow(l - .0894841775 * a - 1.291485548 * b, 3);
                    color = new(Gamma(4.0767416621 * ll - 3.3077115913 * mm + .2309699292 * ss), Gamma(-1.2684380046 * ll + 2.6097574011 * mm - .3413193965 * ss), Gamma(-.0041960863 * ll - .7034186147 * mm + 1.707614701 * ss), alpha); break;
                case "color" when space is "srgb" or "srgb-linear" or "display-p3":
                    var red = Number(parts[0]); var green = Number(parts[1]); var blue = Number(parts[2]);
                    if (space == "display-p3")
                    {
                        var r = Linear(red); var g = Linear(green); var bl = Linear(blue);
                        color = new(Gamma(1.2249401763 * r - .2249401763 * g), Gamma(-.0420569612 * r + 1.0420569612 * g), Gamma(-.0196375548 * r - .0786360655 * g + 1.0982736203 * bl), alpha);
                    }
                    else color = space == "srgb" ? new(Clamp(red), Clamp(green), Clamp(blue), alpha) : new(Gamma(red), Gamma(green), Gamma(blue), alpha);
                    break;
                default: return false;
            }
            return double.IsFinite(color.R) && double.IsFinite(color.G) && double.IsFinite(color.B);
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return false; }
    }
}
