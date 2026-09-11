using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void DesktopAndRemoteProtocolIncludesDurableTerminalsAndSharedIcons()
    {
        Assert.Equal(60, ProtocolVersion.Current);
    }
}
