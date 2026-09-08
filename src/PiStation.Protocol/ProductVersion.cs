using System.Reflection;

namespace PiStation.Protocol;

public static class ProductVersion
{
    public static string Current { get; } = typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
