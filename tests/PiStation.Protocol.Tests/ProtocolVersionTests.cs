using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void DesktopFeatureProtocolVersionIsFortyThree()
    {
        Assert.Equal(43, ProtocolVersion.Current);
    }
}
