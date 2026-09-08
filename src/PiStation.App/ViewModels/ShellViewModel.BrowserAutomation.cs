using PiStation.ClientRuntime;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    internal Task<BrowserAutomationSession> OpenBrowserAutomationAsync(OpenBrowserAutomationRequest request, CancellationToken token) =>
        RequireClient().OpenBrowserAutomationAsync(request, token);
}
