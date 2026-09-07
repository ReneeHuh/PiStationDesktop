using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void SettlementProtocolVersionIsThirtyTwo()
    {
        Assert.Equal(32, ProtocolVersion.Current);
    }
}
