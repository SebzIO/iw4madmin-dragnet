using System.Reflection;

namespace Dragnet;

public static class DragnetBuildInfo
{
    public static string Version =>
        typeof(DragnetBuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0] ?? "0.0.0";

    public const string RepositoryUrl = "https://github.com/SebzIO/iw4madmin-dragnet";
}
