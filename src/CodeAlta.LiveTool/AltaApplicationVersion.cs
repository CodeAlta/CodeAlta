using System.Reflection;

namespace CodeAlta.LiveTool;

/// <summary>
/// The version of the application that runs the live tool. Only the applications are versioned: the
/// libraries, this one included, keep the default version.
/// </summary>
/// <param name="Application">The name of the application assembly.</param>
/// <param name="InformationalVersion">The full version, with its build metadata when it has one.</param>
/// <param name="PackageVersion">The version without build metadata.</param>
internal sealed record AltaApplicationVersion(string Application, string InformationalVersion, string PackageVersion)
{
    /// <summary>Reads the version of an application, by default the one that started this process.</summary>
    public static AltaApplicationVersion Read(Assembly? application = null)
    {
        application ??= Assembly.GetEntryAssembly() ?? typeof(AltaApplicationVersion).Assembly;
        var name = application.GetName();
        var informationalVersion = application.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            informationalVersion = name.Version?.ToString() ?? "0.0.0";
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return new AltaApplicationVersion(
            name.Name ?? "alta",
            informationalVersion,
            plus > 0 ? informationalVersion[..plus] : informationalVersion);
    }
}
