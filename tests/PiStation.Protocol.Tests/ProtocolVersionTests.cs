using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void PiResourceManagementProtocolVersionIsTwentyFour()
    {
        Assert.Equal(24, ProtocolVersion.Current);
    }
}
