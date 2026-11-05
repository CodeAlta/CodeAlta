using System.Reflection;

namespace CodeAlta.Server;

/// <summary>The version of the server, from its assembly.</summary>
internal static class ServerVersion
{
    /// <summary>Gets the version, without the build metadata.</summary>
    public static string Current { get; } =
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
