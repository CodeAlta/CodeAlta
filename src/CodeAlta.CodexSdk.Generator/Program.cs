using System.Diagnostics;
using System.Text;
using CodeAlta.CodexSdk;
using CodeAlta.CodexSdk.Generator;

const string schemaFolderName = "codex_app-server_schema";
const string combinedSchemaFileName = "codex_app_server_protocol.schemas.json";
const string defaultNamespace = "CodeAlta.CodexSdk";

string? schemaFile = null;
string? outputDir = null;
var rootNamespace = defaultNamespace;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--schema" or "-s" when i + 1 < args.Length:
            schemaFile = args[++i];
            break;
        case "--output" or "-o" when i + 1 < args.Length:
            outputDir = args[++i];
            break;
        case "--namespace" or "-n" when i + 1 < args.Length:
            rootNamespace = args[++i];
            break;
    }
}

// Default schema location: next to the running executable
var exeDir = AppContext.BaseDirectory;
var schemaDir = Path.Combine(exeDir, schemaFolderName);
var codexPath = CodexProcessHelper.ResolveCodexExecutable(tryFnmLookup: true);
var codexVersionInfo = await CodexVersionDetector.DetectAsync(codexPath).ConfigureAwait(false);

if (schemaFile is null)
{
    var candidate = Path.Combine(schemaDir, combinedSchemaFileName);
    if (!File.Exists(candidate))
    {
        Console.WriteLine($"Schema not found at {candidate}");
        Console.WriteLine("Generating schema via: codex app-server generate-json-schema ...");

        Directory.CreateDirectory(schemaDir);

        // Resolve the codex executable. On Windows it may be a .ps1/.cmd shim
        // installed via fnm/npm, so fall back to invoking through the shell.
        ProcessStartInfo psi;
        if (codexPath != null)
        {
            psi = CodexProcessHelper.CreateCommandProcessStartInfo(
                codexPath,
                $"app-server generate-json-schema --out \"{schemaDir}\"");
        }
        else if (OperatingSystem.IsWindows())
        {
            // Use pwsh to resolve PATH shims (.ps1 / .cmd wrappers)
            psi = CodexProcessHelper.CreateCommandProcessStartInfo(
                "pwsh",
                $"-NoProfile -NonInteractive -Command \"codex app-server generate-json-schema --out '{schemaDir}'\"");
        }
        else
        {
            psi = CodexProcessHelper.CreateCommandProcessStartInfo(
                "/bin/sh",
                $"-c \"codex app-server generate-json-schema --out '{schemaDir}'\"");
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start codex process.");
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            Console.Error.WriteLine($"codex exited with code {proc.ExitCode}: {stderr}");
            return 1;
        }

        Console.WriteLine("Schema generated successfully.");
    }
    else
    {
        Console.WriteLine("Schema folder already exists, skipping generation.");
    }

    schemaFile = candidate;
}

// Default output: src/CodeAlta.CodexSdk/generated (relative to repo root)
outputDir ??= Path.GetFullPath(
    Path.Combine(exeDir, "..", "..", "..", "..", "CodeAlta.CodexSdk", "generated"));

Console.WriteLine($"Schema:    {schemaFile}");
Console.WriteLine($"Output:    {outputDir}");
Console.WriteLine($"Namespace: {rootNamespace}");
Console.WriteLine(
    $"Codex:     {(codexVersionInfo.IsDetected ? codexVersionInfo.Version.ToString() : "unknown")} " +
    $"(raw: {codexVersionInfo.RawOutput})");
Console.WriteLine();

// Load all definitions
var defs = await SchemaWalker.LoadDefinitionsAsync(schemaFile, rootNamespace);
Console.WriteLine($"Found {defs.Count} type definitions");

// Build emitter with full type registry
var emitter = new CSharpEmitter(defs, rootNamespace);

// Emit all types
var filesByNamespace = emitter.EmitAll(defs);

// Clean output directory before writing
await OutputDirectoryCleaner.CleanAsync(outputDir).ConfigureAwait(false);

// Write files
var totalFiles = 0;
foreach (var (ns, files) in filesByNamespace)
{
    // Map namespace to directory: CodeAlta.CodexSdk -> outputDir,
    // CodeAlta.CodexSdk.V2 -> outputDir/V2
    var relPath = ns == rootNamespace
        ? ""
        : ns[(rootNamespace.Length + 1)..].Replace('.', Path.DirectorySeparatorChar);
    var dir = Path.Combine(outputDir, relPath);
    Directory.CreateDirectory(dir);

    foreach (var (fileName, content) in files)
    {
        var filePath = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(filePath, content).ConfigureAwait(false);
        totalFiles++;
    }
}

Console.WriteLine($"Generated {totalFiles} files in {outputDir}/");

// Generate serializer context
var contextCode = emitter.EmitSerializerContext("CodexJsonSerializerContext");
var contextPath = Path.Combine(outputDir, "CodexJsonSerializerContext.gen.cs");
await File.WriteAllTextAsync(contextPath, contextCode).ConfigureAwait(false);
totalFiles++;

// Generate CodexClient partial metadata
var codexClientCode = EmitCodexClientVersionPartial(rootNamespace, codexVersionInfo);
var codexClientPath = Path.Combine(outputDir, "CodexClient.gen.cs");
await File.WriteAllTextAsync(codexClientPath, codexClientCode).ConfigureAwait(false);
totalFiles++;

Console.WriteLine($"  (includes serializer context with {totalFiles - 1} type registrations)");

// Print warnings about types that fell back to JsonElement
if (emitter.Warnings.Count > 0)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Warnings ({emitter.Warnings.Count}):");
    foreach (var warning in emitter.Warnings)
        Console.WriteLine($"  WARN: {warning}");
    Console.ResetColor();
}

return 0;

static string EmitCodexClientVersionPartial(string rootNamespace, CodexVersionInfo versionInfo)
{
    ArgumentNullException.ThrowIfNull(rootNamespace);

    var escapedRawOutput = EscapeStringLiteral(versionInfo.RawOutput);
    var version = versionInfo.Version;
    var versionCtor = version.Revision >= 0
        ? $"new Version({version.Major}, {version.Minor}, {version.Build}, {version.Revision})"
        : $"new Version({version.Major}, {version.Minor}, {version.Build})";

    var builder = new StringBuilder();
    builder.AppendLine("// <auto-generated/>");
    builder.AppendLine("#nullable enable");
    builder.AppendLine();
    builder.AppendLine("using System;");
    builder.AppendLine();
    builder.AppendLine($"namespace {rootNamespace};");
    builder.AppendLine();
    builder.AppendLine("public sealed partial class CodexClient");
    builder.AppendLine("{");
    builder.AppendLine("    /// <summary>");
    builder.AppendLine("    /// Gets the Codex CLI version used when generating this SDK.");
    builder.AppendLine("    /// </summary>");
    builder.AppendLine($"    public static Version CompiledAgainstVersion {{ get; }} = {versionCtor};");
    builder.AppendLine();
    builder.AppendLine("    /// <summary>");
    builder.AppendLine("    /// Gets the raw output reported by <c>codex --version</c> during generation.");
    builder.AppendLine("    /// </summary>");
    builder.AppendLine($"    public const string CompiledAgainstVersionRaw = \"{escapedRawOutput}\";");
    builder.AppendLine("}");
    return builder.ToString();
}

static string EscapeStringLiteral(string value)
{
    return value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
