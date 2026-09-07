using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void SentMessageContentProtocolVersionIsTwentyNine()
    {
        Assert.Equal(29, ProtocolVersion.Current);
    }
}
