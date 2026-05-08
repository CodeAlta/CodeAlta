using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes an external package version copied into generated plugin root package files.
/// </summary>
public sealed record PluginPackageVersion
{
    /// <summary>Gets the package id.</summary>
    public required string Include { get; init; }

    /// <summary>Gets the package version.</summary>
    public required string Version { get; init; }
}

/// <summary>
/// Provides package versions copied from CodeAlta source package metadata into plugin roots.
/// </summary>
public static partial class PluginPackageVersionProvider
{
    /// <summary>Start marker used in <c>Directory.Packages.props</c>.</summary>
    public const string StartMarker = "CodeAltaPluginPackageVersions:Start";

    /// <summary>End marker used in <c>Directory.Packages.props</c>.</summary>
    public const string EndMarker = "CodeAltaPluginPackageVersions:End";

    /// <summary>
    /// Extracts marker-delimited plugin package versions from CodeAlta's central package file.
    /// </summary>
    /// <param name="content">The <c>Directory.Packages.props</c> content.</param>
    /// <returns>Package versions found inside the marker block.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown when exactly one marker is present or the marker order is invalid.</exception>
    public static IReadOnlyList<PluginPackageVersion> ExtractPluginPackageVersions(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var startIndex = content.IndexOf(StartMarker, StringComparison.Ordinal);
        var endIndex = content.IndexOf(EndMarker, StringComparison.Ordinal);
        if (startIndex < 0 && endIndex < 0)
        {
            return [];
        }

        if (startIndex < 0 || endIndex < 0 || endIndex <= startIndex)
        {
            throw new FormatException("The CodeAlta plugin package version marker block is malformed.");
        }

        var startLineEnd = content.IndexOf('\n', startIndex);
        var blockStart = startLineEnd < 0 ? startIndex + StartMarker.Length : startLineEnd + 1;
        var block = content[blockStart..endIndex];
        var versions = new List<PluginPackageVersion>();
        foreach (Match match in PackageVersionRegex().Matches(block))
        {
            var include = match.Groups["include"].Value;
            var version = match.Groups["version"].Value;
            if (!string.IsNullOrWhiteSpace(include) && !string.IsNullOrWhiteSpace(version))
            {
                versions.Add(new PluginPackageVersion
                {
                    Include = include,
                    Version = version,
                });
            }
        }

        return versions
            .OrderBy(static version => version.Include, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex("<PackageVersion\\s+Include=\\\"(?<include>[^\\\"]+)\\\"\\s+Version=\\\"(?<version>[^\\\"]+)\\\"\\s*/>", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionRegex();

    /// <summary>
    /// Extracts plugin package versions from a file when it exists.
    /// </summary>
    /// <param name="path">The package file path.</param>
    /// <returns>Package versions found inside the marker block, or an empty list when the file does not exist.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is empty.</exception>
    public static IReadOnlyList<PluginPackageVersion> ExtractPluginPackageVersionsFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path)
            ? ExtractPluginPackageVersions(File.ReadAllText(path, Encoding.UTF8))
            : [];
    }
}

/// <summary>
/// Describes options used to generate plugin root build files.
/// </summary>
public sealed record PluginRootBuildFileOptions
{
    /// <summary>Gets the folder containing the running CodeAlta assemblies.</summary>
    public required string CodeAltaExeFolder { get; init; }

    /// <summary>Gets the global.json object content to mirror into plugin roots.</summary>
    public required string GlobalJsonContent { get; init; }

    /// <summary>Gets host CodeAlta assemblies referenced from the default load context.</summary>
    public IReadOnlyList<string> HostAssemblyNames { get; init; } = PluginRootBuildFileGenerator.DefaultHostAssemblyNames;

    /// <summary>Gets shared external authoring package names referenced with runtime and native assets excluded.</summary>
    public IReadOnlyList<string> SharedPackageNames { get; init; } = PluginRootBuildFileGenerator.DefaultSharedPackageNames;

    /// <summary>Gets shared package versions written to generated <c>Directory.Packages.props</c>.</summary>
    public IReadOnlyList<PluginPackageVersion> PackageVersions { get; init; } = [];
}

/// <summary>
/// Describes the result of plugin root build-file generation.
/// </summary>
public sealed record PluginRootBuildFileGenerationResult
{
    /// <summary>Gets the generated files that were created or changed.</summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];

    /// <summary>Gets the generated files that were already up to date.</summary>
    public IReadOnlyList<string> UnchangedFiles { get; init; } = [];

    /// <summary>Gets diagnostics raised during generation.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets a value indicating whether generation completed without errors.</summary>
    public bool Succeeded => Diagnostics.All(static diagnostic => diagnostic.Severity < CodeAlta.Plugins.Abstractions.PluginDiagnosticSeverity.Error);
}

/// <summary>
/// Generates CodeAlta-owned MSBuild support files into plugin roots.
/// </summary>
public sealed class PluginRootBuildFileGenerator
{
    /// <summary>Header written to CodeAlta-generated XML plugin build files.</summary>
    public const string GeneratedXmlHeader = "<!-- This file is generated by CodeAlta. Do not edit directly. -->";

    /// <summary>Marker written to generated JSON files.</summary>
    public const string GeneratedJsonMarker = "This file is generated by CodeAlta. Do not edit directly.";

    private const string GeneratedJsonPropertyName = "_codealta";

    /// <summary>Gets the default host assembly references.</summary>
    public static IReadOnlyList<string> DefaultHostAssemblyNames { get; } =
    [
        "CodeAlta.Plugins.Abstractions",
        "CodeAlta.Agent",
        "CodeAlta.Catalog",
    ];

    /// <summary>Gets the default shared external package references.</summary>
    public static IReadOnlyList<string> DefaultSharedPackageNames { get; } =
    [
        "Microsoft.Extensions.AI.Abstractions",
        "XenoAtom.CommandLine",
        "XenoAtom.Logging",
        "XenoAtom.Terminal.UI",
        "XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp",
        "XenoAtom.Terminal.UI.Extensions.Markdown",
        "XenoAtom.Terminal.UI.Extensions.Screenshot",
        "XenoAtom.Terminal.UI.Graphics",
    ];

    /// <summary>
    /// Generates root-level MSBuild files for a plugin root.
    /// </summary>
    /// <param name="root">The plugin root.</param>
    /// <param name="options">Generation options.</param>
    /// <param name="cancellationToken">A token to cancel generation.</param>
    /// <returns>The generation result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="root"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">Thrown when <see cref="PluginRootBuildFileOptions.GlobalJsonContent"/> is not a JSON object.</exception>
    public async ValueTask<PluginRootBuildFileGenerationResult> GenerateAsync(
        PluginRoot root,
        PluginRootBuildFileOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(root.RootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CodeAltaExeFolder);
        ArgumentNullException.ThrowIfNull(options.GlobalJsonContent);

        Directory.CreateDirectory(root.RootPath);
        var written = new List<string>();
        var unchanged = new List<string>();
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var lockPath = Path.Combine(root.RootPath, ".codealta.plugins.lock");
        await using var lockStream = await AcquireRootLockAsync(lockPath, cancellationToken).ConfigureAwait(false);

        foreach (var file in CreateFiles(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(root.RootPath, file.FileName);
            try
            {
                var status = await WriteGeneratedFileIfChangedAsync(path, file.Content, file.Marker, cancellationToken).ConfigureAwait(false);
                if (status == GeneratedFileWriteStatus.Written)
                {
                    written.Add(path);
                }
                else
                {
                    unchanged.Add(path);
                }
            }
            catch (InvalidOperationException ex)
            {
                diagnostics.Add(PluginRuntimeDiagnostic.Error(
                    PluginRuntimeDiagnosticSource.RootGeneration,
                    ex.Message,
                    path: path,
                    exception: ex));
            }
        }

        return new PluginRootBuildFileGenerationResult
        {
            WrittenFiles = written,
            UnchangedFiles = unchanged,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Computes a SHA-256 hash for generated root files.
    /// </summary>
    /// <param name="rootPath">The plugin root path.</param>
    /// <returns>File hashes keyed by file name for existing generated files.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootPath"/> is empty.</exception>
    public static IReadOnlyDictionary<string, string> ComputeGeneratedFileHashes(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
        {
            var path = Path.Combine(rootPath, fileName);
            if (File.Exists(path))
            {
                hashes[fileName] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            }
        }

        return hashes;
    }

    private static IReadOnlyList<GeneratedFile> CreateFiles(PluginRootBuildFileOptions options)
    {
        var codeAltaExeFolder = XmlEscape(PluginRuntimePathService.NormalizeDirectory(options.CodeAltaExeFolder));
        var packageVersions = options.PackageVersions.ToDictionary(static version => version.Include, StringComparer.OrdinalIgnoreCase);
        var props = $"""
{GeneratedXmlHeader}
<Project>
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>preview</LangVersion>
    <IsPackable>false</IsPackable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CodeAltaExeFolder Condition="'$(CodeAltaExeFolder)' == ''">{codeAltaExeFolder}</CodeAltaExeFolder>
    <CodeAltaPluginRoot>$(MSBuildThisFileDirectory)</CodeAltaPluginRoot>
  </PropertyGroup>
</Project>
""";

        var targets = new StringBuilder();
        targets.AppendLine(GeneratedXmlHeader);
        targets.AppendLine("<Project>");
        targets.AppendLine("  <ItemGroup>");
        foreach (var assemblyName in options.HostAssemblyNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
        {
            targets.AppendLine($"    <Reference Include=\"{XmlEscape(assemblyName)}\">");
            targets.AppendLine($"      <HintPath>$(CodeAltaExeFolder)\\{XmlEscape(assemblyName)}.dll</HintPath>");
            targets.AppendLine("      <Private>false</Private>");
            targets.AppendLine("    </Reference>");
        }

        targets.AppendLine("  </ItemGroup>");
        targets.AppendLine("  <ItemGroup>");
        foreach (var packageName in options.SharedPackageNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (packageVersions.TryGetValue(packageName, out var packageVersion))
            {
                targets.AppendLine($"    <PackageReference Include=\"{XmlEscape(packageName)}\" Version=\"{XmlEscape(packageVersion.Version)}\">");
            }
            else
            {
                targets.AppendLine($"    <PackageReference Include=\"{XmlEscape(packageName)}\">");
            }

            targets.AppendLine("      <ExcludeAssets>runtime;native</ExcludeAssets>");
            targets.AppendLine("    </PackageReference>");
        }

        targets.AppendLine("  </ItemGroup>");
        targets.AppendLine("  <Target Name=\"CodeAltaPluginTargetPath\" AfterTargets=\"Build\" Returns=\"@(CodeAltaPluginTargetPath)\" Outputs=\"$(TargetPath)\" Condition=\"'$(TargetPath)' != ''\">");
        targets.AppendLine("    <ItemGroup>");
        targets.AppendLine("      <CodeAltaPluginTargetPath Include=\"$(TargetPath)\" />");
        targets.AppendLine("    </ItemGroup>");
        targets.AppendLine("    <Message Text=\"CodeAltaPluginTargetPath=$(TargetPath)\" Importance=\"High\" />");
        targets.AppendLine("  </Target>");
        targets.AppendLine("</Project>");

        var packages = new StringBuilder();
        packages.AppendLine(GeneratedXmlHeader);
        packages.AppendLine("<Project>");
        packages.AppendLine("  <PropertyGroup>");
        packages.AppendLine("    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>");
        packages.AppendLine("    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>");
        packages.AppendLine("  </PropertyGroup>");
        packages.AppendLine("</Project>");

        var globalJson = NormalizeGlobalJson(options.GlobalJsonContent);
        return
        [
            new GeneratedFile("Directory.Build.props", props, GeneratedXmlHeader),
            new GeneratedFile("Directory.Build.targets", targets.ToString(), GeneratedXmlHeader),
            new GeneratedFile("Directory.Packages.props", packages.ToString(), GeneratedXmlHeader),
            new GeneratedFile("global.json", globalJson, GeneratedJsonMarker),
        ];
    }

    private static async ValueTask<FileStream> AcquireRootLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<GeneratedFileWriteStatus> WriteGeneratedFileIfChangedAsync(
        string path,
        string content,
        string marker,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (!existing.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to overwrite user-owned plugin root file '{path}'.");
            }

            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return GeneratedFileWriteStatus.Unchanged;
            }
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
        return GeneratedFileWriteStatus.Written;
    }

    private static string NormalizeGlobalJson(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        var legacyComment = "// " + GeneratedJsonMarker;
        if (normalized.StartsWith(legacyComment, StringComparison.Ordinal))
        {
            normalized = normalized[legacyComment.Length..].TrimStart('\n');
        }

        var json = JsonNode.Parse(
            normalized,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new FormatException("The plugin root global.json content must be a JSON object.");
        json[GeneratedJsonPropertyName] = GeneratedJsonMarker;
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string XmlEscape(string value) => SecurityElementEscape(value);

    private static string SecurityElementEscape(string value)
        => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private sealed record GeneratedFile(string FileName, string Content, string Marker);

    private enum GeneratedFileWriteStatus
    {
        Unchanged,
        Written,
    }
}
