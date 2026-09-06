using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void PiPlanWorkflowProtocolVersionIsTwentySix()
    {
        Assert.Equal(26, ProtocolVersion.Current);
    }
}
