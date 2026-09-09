using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void RemoteHostFileAndRuntimeProtocolVersionIsFortyTwo()
    {
        Assert.Equal(42, ProtocolVersion.Current);
    }
}
