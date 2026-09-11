using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PiStation.App.ViewModels;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Projections;

namespace PiStation.App.Views.Controls;

public sealed partial class ConversationTimeline
{
    private readonly HashSet<string> _componentInputs = [];

    private async void OnComponentResizeClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (sender is Button { DataContext: QuestionTimelineItemViewModel { IsPending: true } question, Tag: string width } && int.TryParse(width, out var columns))
            await ViewModel.AnswerQuestionAsync(InteractionId.Parse(question.InteractionId),
                "[pistation:event]" + System.Text.Json.JsonSerializer.Serialize(new { type = "resize", columns, rows = 40 }));
    }

    private async void OnComponentPointerPressed(object sender, PointerRoutedEventArgs args) =>
        await SendComponentPointerAsync(sender, args, false);

    private async void OnComponentPointerWheelChanged(object sender, PointerRoutedEventArgs args) =>
        await SendComponentPointerAsync(sender, args, true);

    private async Task SendComponentPointerAsync(object sender, PointerRoutedEventArgs args, bool wheel)
    {
        if (sender is not ContentControl { DataContext: QuestionTimelineItemViewModel { InputKind: QuestionInputKind.Component, IsPending: true } question } control ||
            !_componentInputs.Add(question.InteractionId)) return;
        try
        {
            var point = args.GetCurrentPoint(control);
            var measure = new TextBlock { Text = "M", FontFamily = new FontFamily("Consolas"), FontSize = 14 };
            measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var column = Math.Clamp((int)Math.Floor(point.Position.X / Math.Max(1, measure.DesiredSize.Width)) + 1, 1, 240);
            var row = Math.Clamp((int)Math.Floor(point.Position.Y / 20) + 1, 1, 120);
            var button = wheel ? point.Properties.MouseWheelDelta > 0 ? 64 : 65 : point.Properties.IsRightButtonPressed ? 2 : point.Properties.IsMiddleButtonPressed ? 1 : 0;
            args.Handled = true;
            var value = "[pistation:event]" + System.Text.Json.JsonSerializer.Serialize(new { type = "mouse", column, row, button });
            await ViewModel.AnswerQuestionAsync(InteractionId.Parse(question.InteractionId), value);
        }
        finally { _componentInputs.Remove(question.InteractionId); }
    }
}
