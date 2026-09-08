using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void SourceControlWriterProtocolVersionIsThirtyFive()
    {
        Assert.Equal(38, ProtocolVersion.Current);
    }
}
