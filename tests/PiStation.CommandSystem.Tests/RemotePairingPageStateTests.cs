using System.Buffers.Binary;
using PiStation.App.Composition;
using PiStation.Protocol.Models;

namespace PiStation.CommandSystem.Tests;

public sealed class RemotePairingPageStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyLinksCreatedDuringThisVisitAreRecoverable()
    {
        var local = CreatePairing("local");
        var fromCli = CreatePairing("cli");
        var page = new RemotePairingPageState();
        page.Remember(local, page.Generation);
        page.Synchronize([local.Invitation, fromCli.Invitation], sharing: true, Now);
        Assert.Equal(local.Url, page.GetUrl(local.Invitation.Id, Now));
        Assert.Null(page.GetUrl(fromCli.Invitation.Id, Now));
        page.Clear();
        page.Synchronize([local.Invitation, fromCli.Invitation], sharing: true, Now);
        Assert.Null(page.GetUrl(local.Invitation.Id, Now));
        Assert.Null(page.GetUrl(fromCli.Invitation.Id, Now));
    }

    [Fact]
    public void LateCreationCannotRestoreASecretAfterLeavingAndReopeningSettings()
    {
        var page = new RemotePairingPageState();
        var previousVisit = page.Generation;
        var pairing = CreatePairing("late-result");
        page.Clear();
        page.Synchronize([pairing.Invitation], sharing: true, Now);
        Assert.False(page.Remember(pairing, previousVisit));
        Assert.Null(page.GetUrl(pairing.Invitation.Id, Now));
        var newlyCreated = CreatePairing("current-visit");
        Assert.True(page.Remember(newlyCreated, page.Generation));
        Assert.NotNull(page.GetUrl(newlyCreated.Invitation.Id, Now));
    }

    [Fact]
    public void RemovedLinkCannotBeCopiedEvenIfRefreshSelectsAnotherLink()
    {
        var clicked = CreatePairing("clicked");
        var remaining = CreatePairing("remaining");
        var page = new RemotePairingPageState();
        page.Remember(clicked, page.Generation);
        page.Remember(remaining, page.Generation);
        // Consumption, individual revocation, and CLI changes all remove the unused row.
        page.Synchronize([remaining.Invitation], sharing: true, Now);
        Assert.Null(page.GetUrl(clicked.Invitation.Id, Now));
        Assert.Equal(remaining.Url, page.GetUrl(remaining.Invitation.Id, Now));
        Assert.Null(page.GetUrl(null, Now));
    }

    [Fact]
    public void ExpiredLinksAreClearedEvenBeforeTheNextRefresh()
    {
        var pairing = CreatePairing("expires");
        var page = new RemotePairingPageState();
        page.Remember(pairing, page.Generation);
        Assert.NotNull(page.GetUrl(pairing.Invitation.Id, pairing.Invitation.ExpiresAt.AddTicks(-1)));
        Assert.Null(page.GetUrl(pairing.Invitation.Id, pairing.Invitation.ExpiresAt));
        // Moving the clock back must not recover a discarded secret.
        Assert.Null(page.GetUrl(pairing.Invitation.Id, Now));
    }

    [Fact]
    public void RefreshRemovesExpiredSecretsAndStoppingSharingClearsAllSecrets()
    {
        var expired = CreatePairing("expired");
        var active = CreatePairing("active");
        active = active with { Invitation = active.Invitation with { ExpiresAt = Now.AddHours(1) } };
        var page = new RemotePairingPageState();
        page.Remember(expired, page.Generation);
        page.Remember(active, page.Generation);
        page.Synchronize([expired.Invitation, active.Invitation], sharing: true, Now.AddMinutes(5));
        Assert.Null(page.GetUrl(expired.Invitation.Id, Now));
        Assert.NotNull(page.GetUrl(active.Invitation.Id, Now));
        page.Synchronize([active.Invitation], sharing: false, Now);
        page.Synchronize([active.Invitation], sharing: true, Now);
        Assert.Null(page.GetUrl(active.Invitation.Id, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1440)]
    public void UiLifetimeSupportsWholeMinutesThroughOneDay(double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RemotePairingPageState.LifetimeFromMinutes(minutes));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(1441)]
    public void InvalidUiLifetimesAreRejected(double minutes) =>
        Assert.Throws<ArgumentException>(() => RemotePairingPageState.LifetimeFromMinutes(minutes));

    [Fact]
    public void LabelsAreTrimmedAndBlankLabelsUseTheOptionalDefault()
    {
        Assert.Equal("Travel laptop", RemotePairingPageState.NormalizeLabel("  Travel laptop  "));
        Assert.Null(RemotePairingPageState.NormalizeLabel("   "));
        Assert.Throws<ArgumentException>(() => RemotePairingPageState.NormalizeLabel("new\nline"));
        Assert.Throws<ArgumentException>(() => RemotePairingPageState.NormalizeLabel(new string('a', 81)));
    }

    [Fact]
    public void MetadataRowsExposeUsefulDetailsButNotPairingSecrets()
    {
        var pairing = CreatePairing("invitation");
        var invitation = new RemoteInvitationRow(pairing.Invitation);
        Assert.Equal("Travel laptop", invitation.Name);
        Assert.Contains("Read only", invitation.Summary, StringComparison.Ordinal);
        Assert.Contains("invitation", invitation.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(pairing.Url, invitation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pairing.Url, pairing.ToString(), StringComparison.Ordinal);
        Assert.Equal("Unlabelled pairing link", new RemoteInvitationRow(pairing.Invitation with { Label = null }).Name);

        var session = new RemoteSessionRow(new("session-id", "Automation", RemoteAccessLevel.Operate, Now.AddHours(1), "build-agent"));
        Assert.Equal("Automation", session.Name);
        Assert.Contains("Operate", session.Summary, StringComparison.Ordinal);
        Assert.Contains("build-agent", session.Details, StringComparison.Ordinal);
        Assert.Contains("session-id", session.Details, StringComparison.Ordinal);
        Assert.Contains("Not specified", new RemoteSessionRow(session.Device with { Subject = null }).Details, StringComparison.Ordinal);
    }

    [Fact]
    public void QrIsAnInMemorySquarePngAndChangesWithTheCredential()
    {
        var pairing = CreatePairing("qr");
        var png = RemotePairingPageState.CreateQrPng(pairing.Url);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        Assert.Equal(width, height);
        Assert.InRange(width, 84, 1024);
        Assert.Equal(0, width % 4);
        var another = RemoteInvitation.Parse(pairing.Url) with { Token = new string('C', 64) };
        Assert.False(png.SequenceEqual(RemotePairingPageState.CreateQrPng(another.Encode())));
        Assert.Throws<ArgumentException>(() => RemotePairingPageState.CreateQrPng("not a pairing link"));
    }

    private static CreatedRemotePairing CreatePairing(string id) => new(
        new(id, "Travel laptop", RemoteAccessLevel.ReadOnly, Now.AddMinutes(5)),
        new RemoteInvitation(new Uri("https://192.0.2.10:52740/"), new string('B', 64), new string('A', 64)).Encode());
}
