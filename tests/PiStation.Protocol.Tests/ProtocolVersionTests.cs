using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CombinedDesktopAndRemoteProtocolVersionIsFortyFive()
    {
        Assert.Equal(46, ProtocolVersion.Current);
    }
}
