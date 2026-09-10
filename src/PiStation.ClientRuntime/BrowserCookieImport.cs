using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PiStation.ClientRuntime;

public sealed record BrowserImportSource(string Browser, string Name, string Directory, string Database, string? KeyFile, string? UnavailableReason)
{
    public string DisplayName => $"{Browser} / {Name}" + (UnavailableReason is null ? "" : " — " + UnavailableReason);
}
public sealed record ImportedBrowserCookie(string Host, string Name, string Value, string Path, bool Secure,
    bool HttpOnly, double? Expires, string? SameSite)
{
    public override string ToString() => "Imported browser cookie (value redacted)";
    // Omitting domain for host-only cookies preserves __Host- cookies and prevents subdomain widening.
    public Dictionary<string, object> Parameters()
    {
        var result = new Dictionary<string, object> { ["url"] = (Secure ? "https://" : "http://") + Host.TrimStart('.') + Path,
            ["name"] = Name, ["value"] = Value, ["path"] = Path, ["secure"] = Secure, ["httpOnly"] = HttpOnly };
        if (Host.StartsWith('.')) result["domain"] = Host;
        if (Expires is { } expiry) result["expires"] = expiry;
        if (SameSite is not null) result["sameSite"] = SameSite;
        return result;
    }
}
public sealed record BrowserCookieReadResult(IReadOnlyList<ImportedBrowserCookie> Cookies, int Skipped);

public static class BrowserCookieImport
{
    public static IReadOnlyList<BrowserImportSource> Discover() => Discover(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static IReadOnlyList<BrowserImportSource> Discover(string roaming, string local)
    {
        var sources = new List<BrowserImportSource>();
        var firefox = System.IO.Path.Combine(roaming, "Mozilla", "Firefox");
        var ini = System.IO.Path.Combine(firefox, "profiles.ini");
        if (File.Exists(ini) && new FileInfo(ini).Length <= 1024 * 1024)
        {
            Dictionary<string, string>? section = null;
            void Add()
            {
                if (section is null || !section.TryGetValue("Path", out var path)) return;
                try
                {
                    var folder = System.IO.Path.GetFullPath(section.GetValueOrDefault("IsRelative") == "1" ? System.IO.Path.Combine(firefox, path) : path);
                    var database = System.IO.Path.Combine(folder, "cookies.sqlite");
                    if (File.Exists(database)) sources.Add(new("Firefox", section.GetValueOrDefault("Name") ?? System.IO.Path.GetFileName(folder), folder, database, null, null));
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
            }
            foreach (var line in File.ReadLines(ini))
            {
                var value = line.Trim();
                if (value.StartsWith('[')) { Add(); section = value.StartsWith("[Profile", StringComparison.Ordinal) ? new(StringComparer.Ordinal) : null; }
                else if (section is not null && value.IndexOf('=') is var index && index > 0) section[value[..index]] = value[(index + 1)..];
            }
            Add();
        }
        var helium = System.IO.Path.Combine(local, "imput", "Helium", "User Data");
        if (System.IO.Directory.Exists(helium))
            foreach (var folder in System.IO.Directory.EnumerateDirectories(helium).Take(128))
            {
                var name = System.IO.Path.GetFileName(folder);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                var database = System.IO.Path.Combine(folder, "Network", "Cookies");
                if (!File.Exists(database)) database = System.IO.Path.Combine(folder, "Cookies");
                if (File.Exists(database)) sources.Add(new("Helium", name, folder, database, System.IO.Path.Combine(helium, "Local State"), null));
            }
        return sources.DistinctBy(source => source.Database, StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    }

    public static BrowserCookieReadResult ReadSelected(BrowserImportSource source, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installed-browser import is supported on Windows.");
        var current = Discover().SingleOrDefault(item => item.Database == source.Database && item.Browser == source.Browser)
            ?? throw new InvalidOperationException("The source profile changed. Refresh the source list.");
        if (current.UnavailableReason is not null) throw new InvalidOperationException(current.UnavailableReason);
        return ReadDatabase(current, token);
    }

    internal static BrowserCookieReadResult ReadDatabase(BrowserImportSource source, CancellationToken token)
    {
        if (new FileInfo(source.Database).Length > 128 * 1024 * 1024) throw new IOException("The cookie database exceeds 128 MiB.");
        var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pistation-cookie-" + Guid.NewGuid().ToString("N") + ".sqlite");
        byte[]? key = null;
        try
        {
            token.ThrowIfCancellationRequested();
            using (var original = Open(source.Database))
            using (var copy = original.CreateCommand())
            {
                copy.CommandText = "VACUUM INTO $target";
                copy.Parameters.AddWithValue("$target", temporary);
                copy.CommandTimeout = 5;
                copy.ExecuteNonQuery();
            }
            using var database = Open(temporary);
            var firefox = source.Browser == "Firefox";
            using var versionCommand = database.CreateCommand();
            versionCommand.CommandText = firefox ? "PRAGMA user_version" : "SELECT value FROM meta WHERE key='version'";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            var table = firefox ? "moz_cookies" : "cookies";
            using var schema = database.CreateCommand();
            schema.CommandText = "PRAGMA table_info(" + table + ")";
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
            string Column(string name, string fallback) => columns.Contains(name) ? name : fallback;
            using var select = database.CreateCommand();
            select.CommandText = firefox
                ? $"SELECT host,name,value,path,expiry,isSecure,isHttpOnly,{Column("sameSite", "NULL")},{(version is >= 10 and <= 14 ? Column("rawSameSite", "NULL") : "NULL")},{Column("originAttributes", "''")} FROM moz_cookies LIMIT 10001"
                : $"SELECT host_key,name,value,path,expires_utc,is_secure,is_httponly,{Column("samesite", "-1")},encrypted_value,{Column("top_frame_site_key", "''")} FROM cookies LIMIT 10001";
            if (!firefox) key = ReadHeliumKey(source.KeyFile);
            var cookies = new List<ImportedBrowserCookie>();
            var skipped = 0;
            long total = 0;
            using var rows = select.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                if (cookies.Count + skipped >= 10000) throw new IOException("The profile exceeds 10,000 cookies; import a smaller profile.");
                try
                {
                    var host = rows.GetString(0); var name = rows.GetString(1); var value = rows.GetString(2); var path = rows.GetString(3);
                    if (!rows.IsDBNull(9) && rows.GetString(9).Length > 0) { skipped++; continue; }
                    if (!firefox && rows.GetFieldValue<byte[]>(8) is { Length: > 0 } encrypted)
                        value = DecryptHelium(encrypted, key, host, version);
                    var secure = rows.GetInt64(5) != 0;
                    var expiry = rows.GetInt64(4);
                    double? expires = expiry <= 0 ? null : firefox ? (version >= 16 ? expiry / 1000d : expiry) : expiry / 1_000_000d - 11644473600;
                    if (expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { skipped++; continue; }
                    var site = rows.IsDBNull(7) ? -1 : rows.GetInt32(7);
                    var raw = firefox && !rows.IsDBNull(8) ? rows.GetInt32(8) : -1;
                    var sameSite = firefox ? (site == 1 && raw == 0 ? null : site switch { 0 => "None", 1 => "Lax", 2 => "Strict", _ => null })
                        : site switch { 0 => "None", 1 => "Lax", 2 => "Strict", _ => null };
                    var bare = host.TrimStart('.');
                    if (host.Length > 254 || Uri.CheckHostName(bare) == UriHostNameType.Unknown || host.StartsWith("..", StringComparison.Ordinal) ||
                        !path.StartsWith('/') || path.Any(char.IsControl) || name.Any(char.IsControl) || value.Length > 4096 || name.Length > 1024 || path.Length > 2048 ||
                        (sameSite == "None" && !secure)) { skipped++; continue; }
                    total += Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(name);
                    if (total > 16 * 1024 * 1024) throw new IOException("Cookie content exceeds 16 MiB.");
                    cookies.Add(new(host, name, value, path, secure, rows.GetInt64(6) != 0, expires, sameSite));
                }
                catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidCastException or DecoderFallbackException) { skipped++; }
            }
            return new(cookies, skipped);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            File.Delete(temporary);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static byte[]? ReadHeliumKey(string? path)
    {
        if (!OperatingSystem.IsWindows() || path is null || !File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            var bytes = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
            return bytes.AsSpan().StartsWith("DPAPI"u8) ? ProtectedData.Unprotect(bytes[5..], null, DataProtectionScope.CurrentUser) : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or KeyNotFoundException or FormatException) { return null; }
    }
    internal static string DecryptHelium(byte[] encrypted, byte[]? key, string host, int version)
    {
        byte[] plaintext;
        if (encrypted.AsSpan().StartsWith("v10"u8))
        {
            if (key is null || encrypted.Length < 31) throw new CryptographicException();
            plaintext = new byte[encrypted.Length - 31];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(encrypted.AsSpan(3, 12), encrypted.AsSpan(15, plaintext.Length), encrypted.AsSpan(encrypted.Length - 16), plaintext);
        }
        else
        {
            if (!OperatingSystem.IsWindows() || encrypted.AsSpan().StartsWith("v"u8)) throw new CryptographicException();
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        }
        try
        {
            if (version < 24) return new UTF8Encoding(false, true).GetString(plaintext);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host));
            if (plaintext.Length < 32 || !CryptographicOperations.FixedTimeEquals(hash, plaintext.AsSpan(0, 32))) throw new CryptographicException();
            return new UTF8Encoding(false, true).GetString(plaintext.AsSpan(32));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
