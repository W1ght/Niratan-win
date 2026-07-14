using System.Reflection;

namespace Niratan.Helpers;

public static class AppInfoHelper
{
    public static string Version { get; } =
        Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "Unknown";
}
