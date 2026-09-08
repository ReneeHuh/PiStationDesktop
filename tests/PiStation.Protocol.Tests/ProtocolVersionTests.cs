using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void SourceControlWriterProtocolVersionIsThirtyFive()
    {
        Assert.Equal(37, ProtocolVersion.Current);
    }
}
