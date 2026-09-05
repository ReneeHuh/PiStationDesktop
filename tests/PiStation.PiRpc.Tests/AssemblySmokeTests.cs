namespace PiStation.PiRpc.Tests;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void ProductionAssemblyLoads()
    {
        Assert.Equal("PiStation.PiRpc", typeof(PiRpcAssembly).Assembly.GetName().Name);
    }
}
