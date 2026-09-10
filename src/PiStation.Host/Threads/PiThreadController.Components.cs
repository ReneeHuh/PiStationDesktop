using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    // The Pi bridge permits one active custom component per runtime. Correlation
    // prevents delayed completion from dismissing a different extension's input.
    private (string ComponentId, InteractionId InteractionId)? _componentInteraction;

    private async Task CloseComponentInteractionAsync(string componentId)
    {
        if (_componentInteraction is not { } current || current.ComponentId != componentId) return;
        _componentInteraction = null;
        await _interactionGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (Journal.Projection.Timeline.OfType<QuestionTimelineItem>().Any(question =>
                question.InteractionId == current.InteractionId && question.State == InteractionState.Pending))
                ResolveInteraction(new InteractionResolvedEvent(current.InteractionId, InteractionState.Canceled));
        }
        finally { _interactionGate.Release(); }
    }
}
