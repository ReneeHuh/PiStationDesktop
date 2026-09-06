using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void PiIntegrationProtocolVersionIsTwentyThree()
    {
        Assert.Equal(23, ProtocolVersion.Current);
    }
}
