using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void SourceControlWriterProtocolVersionIsThirtyFive()
    {
        Assert.Equal(35, ProtocolVersion.Current);
    }
}
