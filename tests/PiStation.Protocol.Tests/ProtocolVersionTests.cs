using PiStation.Protocol;

namespace PiStation.Protocol.Tests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void PullRequestReviewProtocolVersionIsTwentyEight()
    {
        Assert.Equal(28, ProtocolVersion.Current);
    }
}
