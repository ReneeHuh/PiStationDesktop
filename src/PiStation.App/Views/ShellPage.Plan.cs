using System.Text;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace PiStation.App.Views;

public sealed partial class ShellPage
{
    private async void OnPlanActionClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.ManagePlanAsync((sender as FrameworkElement)?.Tag as string ?? "inspect");

    private void OnReloadPlanClicked(object sender, RoutedEventArgs e) => ViewModel.Plan.ReloadEditor();

    private async void OnExportPlanClicked(object sender, RoutedEventArgs e)
    {
        if ((Application.Current as App)?.FindWindow(XamlRoot) is not { } window || ViewModel.Plan.Snapshot is not { } plan) return;
        var picker = new FileSavePicker { SuggestedFileName = "pistation-plan" };
        picker.FileTypeChoices.Add("Markdown plan", [".md"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        try
        {
            if (await picker.PickSaveFileAsync() is not { } file) return;
            var content = plan.Text + "\n\n## Progress\n\n" + string.Join('\n', plan.Steps.Select(step => $"- [{(step.Completed ? "x" : " ")}] {step.Number}. {step.Text}")) + "\n";
            var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false)); File.Move(temporary, file.Path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            ViewModel.Plan.Status = "Exported plan to " + file.Path;
        }
        catch (Exception exception) { ViewModel.Plan.Status = exception.Message; }
    }
}
