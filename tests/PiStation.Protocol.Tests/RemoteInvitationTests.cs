using PiStation.Protocol.Models;

namespace PiStation.Protocol.Tests;

public sealed class RemoteInvitationTests
{
    [Fact]
    public void VerificationCodeIsDeterministicAndBoundToBothInputs()
    {
        var requestId = "request-123";
        var credential = new string('A', 64);
        var code = PairingVerification.ComputeCode(requestId, credential);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(code, PairingVerification.ComputeCode(requestId, credential));
        Assert.NotEqual(code, PairingVerification.ComputeCode("request-124", credential));
        Assert.NotEqual(code, PairingVerification.ComputeCode(requestId, new string('B', 64)));
    }

    [Fact]
    public void PairingSecretIsOnlyInTheFragmentAndRoundTrips()
    {
        var invitation = new RemoteInvitation(new Uri("https://192.168.1.10:52740/"), new string('A', 64), new string('B', 64));
        var encoded = invitation.Encode();
        Assert.Equal(invitation, RemoteInvitation.Parse(encoded));
        Assert.DoesNotContain(invitation.Token, new Uri(encoded).GetLeftPart(UriPartial.Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.10:52740/")]
    [InlineData("https://name:password@example.com/")]
    [InlineData("https://example.com/?token=test")]
    [InlineData("https://example.com/path/")]
    public void UnsafeRemoteOriginsAreRejected(string address) =>
        Assert.Throws<ArgumentException>(() => RemoteEndpoint.Validate(new Uri(address)));
}
