using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PiStation.ClientRuntime.Tests;

public sealed class BrowserCookieImportTests
{
    [Theory]
    [InlineData(14)]
    [InlineData(16)]
    public void FirefoxSnapshotPreservesScopeExpiryAndSameSiteAndSkipsContainers(int version)
    {
        using var directory = new ClientTestDirectory();
        var path = Path.Combine(directory.Path, "cookies.sqlite");
        var expiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = $"PRAGMA user_version={version}; CREATE TABLE moz_cookies(host TEXT,name TEXT,value TEXT,path TEXT,expiry INTEGER,isSecure INTEGER,isHttpOnly INTEGER,sameSite INTEGER,rawSameSite INTEGER,originAttributes TEXT);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO moz_cookies VALUES ('example.test','__Host-session','fixture','/',$expiry,1,1,$site,0,''),('.example.test','domain','fixture','/account',$expiry,1,0,2,2,''),('example.test','container','secret-fixture','/',$expiry,1,1,0,0,'^userContextId=2'),('example.test','expired','fixture','/',1,1,1,0,0,'');";
            command.Parameters.AddWithValue("$expiry", version >= 16 ? expiry * 1000 : expiry);
            command.Parameters.AddWithValue("$site", version >= 16 ? 256 : 1);
            command.ExecuteNonQuery();
        }
        var original = SHA256.HashData(File.ReadAllBytes(path));
        var result = BrowserCookieImport.ReadDatabase(new("Firefox", "fixture", directory.Path, path, null, null), CancellationToken.None);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(2, result.Skipped);
        var host = Assert.Single(result.Cookies, item => item.Name == "__Host-session");
        Assert.False(host.Parameters().ContainsKey("domain"));
        Assert.False(host.Parameters().ContainsKey("sameSite"));
        Assert.Equal(expiry, host.Expires);
        Assert.True(host.HttpOnly);
        Assert.Equal("https://example.test/", host.Parameters()["url"]);
        var domain = Assert.Single(result.Cookies, item => item.Name == "domain");
        Assert.Equal(".example.test", domain.Parameters()["domain"]);
        Assert.Equal("Strict", domain.SameSite);
        Assert.DoesNotContain("fixture", host.ToString());
    }

    [Fact]
    public void HeliumDecryptsCompatibleAesAndVerifiesHostBindingWithoutAcceptingAppBoundValues()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var host = ".example.test";
        var plain = SHA256.HashData(Encoding.UTF8.GetBytes(host)).Concat(Encoding.UTF8.GetBytes("fixture-value")).ToArray();
        var encrypted = new byte[31 + plain.Length];
        "v10"u8.CopyTo(encrypted);
        RandomNumberGenerator.Fill(encrypted.AsSpan(3, 12));
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(encrypted.AsSpan(3, 12), plain, encrypted.AsSpan(15, plain.Length), encrypted.AsSpan(encrypted.Length - 16));
        Assert.Equal("fixture-value", BrowserCookieImport.DecryptHelium(encrypted, key, host, 24));
        Assert.Throws<CryptographicException>(() => BrowserCookieImport.DecryptHelium(encrypted, key, "different.test", 24));
        encrypted[1] = (byte)'2';
        Assert.Throws<CryptographicException>(() => BrowserCookieImport.DecryptHelium(encrypted, key, host, 24));
    }

    [Fact]
    public void DiscoveryRequiresRegisteredFirefoxProfilesAndKnownHeliumDirectories()
    {
        using var directory = new ClientTestDirectory();
        var roaming = directory.CreateDirectory("roaming");
        var local = directory.CreateDirectory("local");
        var firefox = directory.CreateDirectory("roaming/Mozilla/Firefox/Profiles/test");
        File.WriteAllBytes(Path.Combine(firefox, "cookies.sqlite"), []);
        File.WriteAllText(Path.Combine(roaming, "Mozilla/Firefox/profiles.ini"), "[Profile0]\nName=Test profile\nIsRelative=1\nPath=Profiles/test\n[Install123]\nDefault=ignored\n");
        var helium = directory.CreateDirectory("local/imput/Helium/User Data/Default/Network");
        File.WriteAllBytes(Path.Combine(helium, "Cookies"), []);
        Assert.Equal(["Firefox", "Helium"], BrowserCookieImport.Discover(roaming, local).Select(item => item.Browser));
        Assert.Empty(BrowserCookieImport.Discover(directory.Path, directory.Path));
    }
}
