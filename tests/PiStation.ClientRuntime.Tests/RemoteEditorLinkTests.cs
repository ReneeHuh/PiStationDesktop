using PiStation.ClientRuntime.Ssh;
using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime.Tests;

public sealed class RemoteEditorLinkTests
{
    private static SshConnectionProfile Profile => new(Guid.NewGuid(), "Host", "dev@windows", "", null, null, null, ClientId.New());

    [Fact]
    public void LinkUsesTheSshTargetAndEscapesWindowsPathSegments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = RemoteEditorLink.Create(Profile, @"C:\Users\Dev\my project", @"src\file #1.cs");
        Assert.Equal("vscode://vscode-remote/ssh-remote+dev%40windows/C%3A/Users/Dev/my%20project/src/file%20%231.cs", link.OriginalString);
        Assert.Empty(link.Query);
        Assert.Empty(link.Fragment);
    }

    [Fact]
    public void WrongPortsAndPathsCannotSilentlyOpenADifferentTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => RemoteEditorLink.Create(Profile with { Port = 2222 }, @"C:\repo", "file.cs"));
        Assert.Throws<InvalidOperationException>(() => RemoteEditorLink.Create(Profile with { Port = 22 }, @"C:\repo", "file.cs"));
        Assert.Throws<ArgumentException>(() => RemoteEditorLink.Create(Profile, @"C:\repo", @"..\other\file.cs"));
        Assert.Throws<ArgumentException>(() => RemoteEditorLink.Create(Profile, @"C:\repo", @"C:\other\file.cs"));
    }
}
