using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void PiAutomationProtocolVersionIsThirtyFour()
    {
        Assert.Equal(34, ProtocolVersion.Current);
    }
}
