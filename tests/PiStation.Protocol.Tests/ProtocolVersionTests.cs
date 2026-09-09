using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void DesktopFeatureProtocolVersionIsFortyOne()
    {
        Assert.Equal(42, ProtocolVersion.Current);
    }
}
