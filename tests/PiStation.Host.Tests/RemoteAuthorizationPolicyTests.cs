using System.Reflection;
using PiStation.Host.Hubs;
using PiStation.Host.Security;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class RemoteAuthorizationPolicyTests
{
    [Fact]
    public void EveryRpcHasAnExplicitRemotePolicyOrIsDesktopOnly()
    {
        var methods = typeof(EnvironmentHub).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Distinct().Order().ToArray();
        var classified = RemoteAuthorizationFilter.MethodAccess.Keys.Append(nameof(EnvironmentHub.OpenProjectFileInEditor)).Order().ToArray();
        Assert.Equal(methods, classified);
    }

    [Theory]
    [InlineData(nameof(EnvironmentHub.GetComposerDiscovery))]
    [InlineData(nameof(EnvironmentHub.InspectPiSession))]
    [InlineData(nameof(EnvironmentHub.InspectPiSessionPage))]
    [InlineData(nameof(EnvironmentHub.ManagePiResources))]
    [InlineData(nameof(EnvironmentHub.ApplyPiAutomation))]
    [InlineData(nameof(EnvironmentHub.SubmitBackgroundTask))]
    [InlineData(nameof(EnvironmentHub.SubmitPullRequestReview))]
    [InlineData(nameof(EnvironmentHub.GenerateSourceControlText))]
    public void PiStartupAndMutationsRequireOperateAccess(string method) =>
        Assert.Equal(RemoteAccessLevel.Operate, RemoteAuthorizationFilter.MethodAccess[method]);
}
