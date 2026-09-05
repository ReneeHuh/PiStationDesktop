namespace PiStation.ClientRuntime.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void ProductionAssemblyLoads()
    {
        Assert.Equal("PiStation.ClientRuntime", typeof(ClientRuntimeAssembly).Assembly.GetName().Name);
    }
}
