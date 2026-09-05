using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PiStation.App.ViewModels;

namespace PiStation.App.Views;

public sealed class TimelineItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }

    public DataTemplate? ThinkingTemplate { get; set; }

    public DataTemplate? ToolTemplate { get; set; }

    public DataTemplate? StatusTemplate { get; set; }

    public DataTemplate? ErrorTemplate { get; set; }

    public DataTemplate? TurnBoundaryTemplate { get; set; }

    public DataTemplate? CheckpointTemplate { get; set; }

    public DataTemplate? ApprovalTemplate { get; set; }

    public DataTemplate? QuestionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        MessageTimelineItemViewModel => MessageTemplate,
        ThinkingTimelineItemViewModel => ThinkingTemplate,
        ToolActivityGroupTimelineItemViewModel => ToolTemplate,
        StatusTimelineItemViewModel => StatusTemplate,
        ErrorTimelineItemViewModel => ErrorTemplate,
        TurnBoundaryTimelineItemViewModel => TurnBoundaryTemplate,
        CheckpointTimelineItemViewModel => CheckpointTemplate,
        ApprovalTimelineItemViewModel => ApprovalTemplate,
        QuestionTimelineItemViewModel => QuestionTemplate,
        _ => base.SelectTemplateCore(item),
    };
}
