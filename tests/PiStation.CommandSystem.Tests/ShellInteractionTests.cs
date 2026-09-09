using PiStation.App.Composition;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Projections;

namespace PiStation.CommandSystem.Tests;

public sealed class ShellInteractionTests
{
    [Fact]
    public void QuitRequiresFreshPressOrCompletedHoldAndResetsOnLostFocus()
    {
        var gesture = new QuitGesture();
        Assert.True(gesture.KeyDown(0, 0, false));
        Assert.False(gesture.KeyDown(0, 1, true));
        Assert.False(gesture.KeyDown(1, 10, false));
        Assert.False(gesture.KeyUp(1100));
        Assert.False(gesture.KeyDown(1, 2000, false));
        Assert.False(gesture.KeyDown(1, 3300, true));
        Assert.Contains("Release", gesture.Hint(3300));
        Assert.True(gesture.KeyUp(3300));
        Assert.False(gesture.KeyDown(1, 4000, false));
        gesture.Reset();
        Assert.False(gesture.KeyUp(6000));
    }

    [Fact]
    public void DoublePressRejectsAutoRepeatExpiredAttemptAndModeChange()
    {
        var gesture = new QuitGesture();
        Assert.False(gesture.KeyDown(2, 0, false));
        Assert.False(gesture.KeyDown(2, 50, true));
        Assert.False(gesture.KeyUp(60));
        Assert.True(gesture.KeyDown(2, 499, false));
        Assert.False(gesture.KeyDown(2, 1000, false));
        gesture.KeyUp(1100);
        gesture.Expire(1600);
        Assert.False(gesture.IsPending);
        Assert.False(gesture.KeyDown(2, 1700, false));
        gesture.KeyUp(1750);
        Assert.False(gesture.KeyDown(1, 1800, false));
        Assert.False(gesture.KeyUp(1900));
    }

    [Theory]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(false, false, true, false, false, true)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, true, false, true, false, true)]
    [InlineData(true, true, false, true, false, true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, true, true, true, true, false)]
    public void ComposerFocusAndScrollPreferencesAreIndependent(bool focused, bool history, bool blur,
        bool scroll, bool conflict, bool expected) =>
        Assert.Equal(expected, ComposerPresentation.ShouldCollapse(focused, history, blur, scroll, conflict));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposerStaysExpandedWhileAnOwnedDropdownOrFlyoutIsOpen(bool history) =>
        Assert.False(ComposerPresentation.ShouldCollapse(false, history, true, true, false, hasOpenPopup: true));

    [Fact]
    public void HidingSlashSkillsPreservesDollarSearchAndOtherCommands()
    {
        ComposerCommandDescriptor[] commands =
        [
            new("review", "Review code", ComposerCommandSource.Skill),
            new("help", "Show commands", ComposerCommandSource.BuiltIn),
            new("review-prompt", "Review template", ComposerCommandSource.Prompt),
        ];
        Assert.Equal(3, ComposerPresentation.Suggestions(commands, '/', "", true).Count());
        Assert.Equal(["help", "review-prompt"], ComposerPresentation.Suggestions(commands, '/', "", false).Select(c => c.Name));
        Assert.Equal("review", Assert.Single(ComposerPresentation.Suggestions(commands, '$', "CODE", false)).Name);
        Assert.Equal("review-prompt", Assert.Single(ComposerPresentation.Suggestions(commands, '/', "template", false)).Name);
    }

    [Fact]
    public void ComposerSummaryKeepsAttachmentAndContextCounts()
    {
        Assert.Equal("Draft text · 2 attachment(s) · 1 context item(s)", ComposerPresentation.Summary("Draft\n text", 2, 1));
        Assert.Contains("Write a message", ComposerPresentation.Summary("", 0, 0));
        Assert.EndsWith("…", ComposerPresentation.Summary(new string('x', 140), 0, 0));
    }

    [Fact]
    public void InteractionPreferencesRoundTripAndOldSettingsKeepDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PiStation-ShellTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "layout.json");
            File.WriteAllText(path, "{}");
            var model = new ShellLayoutViewModel(path);
            Assert.Equal(1, model.QuitConfirmationModeIndex);
            Assert.False(model.ProactivePanelsEnabled);
            Assert.True(model.ComposerCollapseOnBlur);
            Assert.True(model.ComposerCollapseOnScroll);
            Assert.True(model.ShowSkillsInSlashMenu);
            model.QuitConfirmationModeIndex = 2;
            model.ProactivePanelsEnabled = true;
            model.ComposerCollapseOnBlur = false;
            model.ComposerCollapseOnScroll = false;
            model.ShowSkillsInSlashMenu = false;
            var restored = new ShellLayoutViewModel(path);
            Assert.Equal(2, restored.QuitConfirmationModeIndex);
            Assert.True(restored.ProactivePanelsEnabled);
            Assert.False(restored.ComposerCollapseOnBlur);
            Assert.False(restored.ComposerCollapseOnScroll);
            Assert.False(restored.ShowSkillsInSlashMenu);
            restored.QuitConfirmationModeIndex = 99;
            Assert.Equal(2, restored.QuitConfirmationModeIndex);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ProactiveDiffOpensOnceForLiveCompletionAndWaitsForCheckpoint()
    {
        var state = new ProactivePanels();
        var projection = Projection();
        Assert.Null(state.Observe(projection, true));
        var turn = TurnId.New();
        var completed = projection with
        {
            CompletionSequence = 1,
            Timeline = [new TurnBoundaryTimelineItem("settled", turn, TurnBoundaryKind.Settled)],
        };
        Assert.Null(state.Observe(completed, true));
        completed = completed with { Checkpoints = [Checkpoint(turn)] };
        Assert.Equal(1, state.Observe(completed, true));
        Assert.Null(state.Observe(completed, true));
        state.Reset();
        Assert.Null(state.Observe(completed, true));
    }

    [Fact]
    public void ProactiveDiffIgnoresDisabledModeNewThreadsEpochsAndNoChanges()
    {
        var state = new ProactivePanels();
        var initial = Projection();
        state.Observe(initial, true);
        var turn = TurnId.New();
        var completed = initial with { CompletionSequence = 1,
            Timeline = [new TurnBoundaryTimelineItem("settled", turn, TurnBoundaryKind.Settled)], Checkpoints = [Checkpoint(turn)] };
        Assert.Null(state.Observe(completed, false));
        Assert.Null(state.Observe(completed, true));
        Assert.Null(state.Observe(completed with { ThreadId = ThreadId.New() }, true));
        Assert.Null(state.Observe(completed with { ProjectionEpoch = ProjectionEpoch.New() }, true));
        state.Reset();
        state.Observe(initial, true);
        Assert.Null(state.Observe(completed with { Checkpoints = [Checkpoint(turn) with { Files = [] }] }, true));
    }

    private static ThreadProjection Projection() => new(EnvironmentId.New(), ThreadId.New(), ProjectionEpoch.New(),
        new Sequence(0), ThreadRuntimeState.Ready, null, [], [], "session", null, null, null);

    [Fact]
    public void AutomaticLinkedPrIgnoresNavigationAndMetadataRefreshButDetectsNewTarget()
    {
        var thread = new ThreadDescriptor(EnvironmentId.New(), ThreadId.New(), ProjectId.New(), "Task", "session",
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var link = new PullRequestLink(SourceControlProvider.GitHub, "owner/repo", "1", "https://github.com/owner/repo/pull/1",
            "Open", "PR title", DateTimeOffset.UtcNow);
        var linked = thread with { PullRequest = link };
        Assert.True(ProactivePanels.IsNewLink(thread, linked));
        Assert.False(ProactivePanels.IsNewLink(null, linked));
        Assert.False(ProactivePanels.IsNewLink(thread, linked with { ThreadId = ThreadId.New() }));
        Assert.False(ProactivePanels.IsNewLink(linked, linked with { PullRequest = link with { State = "Merged" } }));
        Assert.True(ProactivePanels.IsNewLink(linked, linked with { PullRequest = link with { Number = "2" } }));
        Assert.False(ProactivePanels.IsNewLink(linked, thread));
    }
    private static ThreadCheckpoint Checkpoint(TurnId turn) => new(turn, 1, "ref", ThreadCheckpointStatus.Ready,
        [new("file.cs", 1, 0)], null, "entry", DateTimeOffset.UtcNow);
}
