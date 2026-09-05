using System.Reflection;

namespace CodeAlta.Tui.Views;

internal sealed record CodeAltaApplicationVersionInfo(
    string InformationalVersion,
    string PackageVersion,
    string? BuildMetadata);

internal static class CodeAltaApplicationInfo
{
    public const string ProductName = "CodeAlta";

    public const string CopyrightOwner = "Alexandre Mutel";

    public static CodeAltaApplicationVersionInfo GetVersionInfo(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(CodeAltaApplicationInfo).Assembly;

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            informationalVersion = assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        var packageVersion = informationalVersion;
        string? buildMetadata = null;
        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            packageVersion = plusIndex == 0 ? informationalVersion : informationalVersion[..plusIndex];
            buildMetadata = plusIndex + 1 < informationalVersion.Length
                ? informationalVersion[(plusIndex + 1)..]
                : null;
        }

        return new CodeAltaApplicationVersionInfo(informationalVersion, packageVersion, buildMetadata);
    }

    public static string GetCopyrightText(DateTimeOffset? now = null)
    {
        var year = (now ?? DateTimeOffset.Now).Year;
        return $"Copyright (c) {year}, {CopyrightOwner}. All rights reserved.";
    }
}
