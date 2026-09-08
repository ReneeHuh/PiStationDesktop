namespace PiStation.Protocol.Models;

public sealed record CreatePullRequestReviewThreadRequest(
    PullRequestReviewTarget Target,
    PiModelSelection? InheritedModel = null,
    PiThinkingLevel? InheritedThinkingLevel = null);
