using System.Runtime.InteropServices;

namespace CodeAlta.Acp;

/// <summary>
/// Resolves ACP registry manifests into local install plans.
/// </summary>
public sealed class AcpInstallResolver
{
    /// <summary>
    /// Resolves an install plan for the current platform.
    /// </summary>
    /// <param name="manifest">Registry manifest.</param>
    /// <returns>The selected install plan.</returns>
    public AcpInstallPlan Resolve(AcpRegistryAgentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Distribution.Binary is { Count: > 0 } binary &&
            binary.TryGetValue(GetCurrentTargetId(), out var binaryPackage))
        {
            return new AcpInstallPlan
            {
                Manifest = manifest,
                Kind = AcpInstallKind.Binary,
                Command = binaryPackage.Command,
                Arguments = binaryPackage.Arguments,
                EnvironmentVariables = binaryPackage.EnvironmentVariables,
                ArchiveUri = new Uri(binaryPackage.Archive, UriKind.Absolute),
                RelativeCommandPath = binaryPackage.Command,
                TargetId = GetCurrentTargetId(),
            };
        }

        if (manifest.Distribution.Npx is { } npx)
        {
            var npxCommand = AcpCommandLocator.FindCommandPath("npx")
                ?? throw new InvalidOperationException("NPX is required for this ACP agent but was not found on PATH.");
            return new AcpInstallPlan
            {
                Manifest = manifest,
                Kind = AcpInstallKind.Npx,
                Command = npxCommand,
                Arguments = BuildPackageArguments(includeYesFlag: true, npx.Package, npx.Arguments),
                EnvironmentVariables = npx.EnvironmentVariables,
                Package = npx.Package,
            };
        }

        if (manifest.Distribution.Uvx is { } uvx)
        {
            var uvxCommand = AcpCommandLocator.FindCommandPath("uvx")
                ?? throw new InvalidOperationException("UVX is required for this ACP agent but was not found on PATH.");
            return new AcpInstallPlan
            {
                Manifest = manifest,
                Kind = AcpInstallKind.Uvx,
                Command = uvxCommand,
                Arguments = BuildPackageArguments(includeYesFlag: false, uvx.Package, uvx.Arguments),
                EnvironmentVariables = uvx.EnvironmentVariables,
                Package = uvx.Package,
            };
        }

        throw new InvalidOperationException($"ACP agent '{manifest.Id}' does not define a supported distribution for the current platform.");
    }

    /// <summary>Returns the target of the registry this process runs on, such as <c>windows-x86_64</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Thrown on another system or architecture.</exception>
    public static string GetCurrentTargetId()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "darwin"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("ACP registry installs are only supported on Windows, macOS, and Linux.");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture '{RuntimeInformation.ProcessArchitecture}'."),
        };
        return $"{os}-{arch}";
    }

    private static IReadOnlyList<string> BuildPackageArguments(
        bool includeYesFlag,
        string package,
        IReadOnlyList<string>? extraArguments)
    {
        var arguments = new List<string>(4 + (extraArguments?.Count ?? 0));
        if (includeYesFlag)
        {
            arguments.Add("--yes");
        }

        arguments.Add(package);
        if (extraArguments is { Count: > 0 })
        {
            arguments.AddRange(extraArguments);
        }

        return arguments;
    }
}
