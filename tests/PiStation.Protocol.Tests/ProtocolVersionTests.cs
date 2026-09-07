using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void AgentWorkflowProtocolVersionIsThirtyThree()
    {
        Assert.Equal(33, ProtocolVersion.Current);
    }
}
