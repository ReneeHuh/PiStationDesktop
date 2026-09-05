using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CommandSystemProtocolVersionIsNineteen()
    {
        Assert.Equal(19, ProtocolVersion.Current);
    }
}
