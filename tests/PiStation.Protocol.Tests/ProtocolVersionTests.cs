using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void DesktopAndRemoteProtocolIncludesDurableTerminalsAndSharedIcons()
    {
        Assert.Equal(50, ProtocolVersion.Current);
    }
}
