namespace PiStation.Host.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void ProductionAssemblyLoads()
    {
        Assert.Equal("PiStation.Host", typeof(HostAssembly).Assembly.GetName().Name);
    }
}
