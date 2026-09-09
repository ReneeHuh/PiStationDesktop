using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void DesktopFeatureProtocolVersionIsFortyFour()
    {
        Assert.Equal(44, ProtocolVersion.Current);
    }
}
