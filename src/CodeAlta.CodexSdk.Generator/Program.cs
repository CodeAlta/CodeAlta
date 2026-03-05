using System.Diagnostics;
using System.Text;
using CodeAlta.CodexSdk;
using CodeAlta.CodexSdk.Generator;

const string schemaFolderName = "codex_app-server_schema";
const string mixedSchemaFileName = "codex_app_server_protocol.schemas.json";
const string flatV2SchemaFileName = "codex_app_server_protocol.v2.schemas.json";
const string defaultNamespace = "CodeAlta.CodexSdk";

string? schemaFile = null;
var rootNamespace = defaultNamespace;
var schemaBundle = SchemaBundle.V2;
var includeExperimentalApi = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--schema" or "-s" when i + 1 < args.Length:
            schemaFile = args[++i];
            break;
        case "--namespace" or "-n" when i + 1 < args.Length:
            rootNamespace = args[++i];
            break;
        case "--bundle" or "-b" when i + 1 < args.Length:
            schemaBundle = ParseSchemaBundle(args[++i]);
            break;
        case "--experimental":
            includeExperimentalApi = true;
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
    var mixedCandidate = Path.Combine(schemaDir, mixedSchemaFileName);
    var v2Candidate = Path.Combine(schemaDir, flatV2SchemaFileName);
    SafeDelete(mixedCandidate);
    SafeDelete(v2Candidate);

    Console.WriteLine($"Schema bundle: {schemaBundle}");
    Console.WriteLine($"Experimental:  {includeExperimentalApi}");
    Console.WriteLine();
    Console.WriteLine("Generating schema via: codex app-server generate-json-schema ...");

    Directory.CreateDirectory(schemaDir);

    // Resolve the codex executable. On Windows it may be a .ps1/.cmd shim
    // installed via fnm/npm, so fall back to invoking through the shell.
    var experimentalFlag = includeExperimentalApi ? " --experimental" : "";
    var codexArgs = $"app-server generate-json-schema --out \"{schemaDir}\"{experimentalFlag}";
    ProcessStartInfo psi;
    if (codexPath != null)
    {
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            codexPath,
            codexArgs);
    }
    else if (OperatingSystem.IsWindows())
    {
        // Use pwsh to resolve PATH shims (.ps1 / .cmd wrappers)
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            "pwsh",
            $"-NoProfile -NonInteractive -Command \"codex {codexArgs.Replace('\"', '\'')}\"");
    }
    else
    {
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            "/bin/sh",
            $"-c \"codex {codexArgs.Replace('\"', '\'')}\"");
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

    schemaFile = ResolveSchemaFile(
        mixedCandidate: mixedCandidate,
        v2Candidate: v2Candidate,
        schemaBundle: schemaBundle,
        includeExperimentalApi: includeExperimentalApi);
}

// Default output: src/CodeAlta.CodexSdk/generated (relative to repo root)
var outputBaseDir = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "CodeAlta.CodexSdk"));
if (!Directory.Exists(outputBaseDir))
{
    Console.WriteLine($"Error: Expected output base directory not found: {outputBaseDir}");
    return 1;
}
var outputDir = Path.Combine(outputBaseDir, "generated");

Console.WriteLine($"Schema:    {schemaFile}");
Console.WriteLine($"Output:    {outputDir}");
Console.WriteLine($"Namespace: {rootNamespace}");
Console.WriteLine(
    $"Codex:     {(codexVersionInfo.IsDetected ? codexVersionInfo.Version.ToString() : "unknown")} " +
    $"(raw: {codexVersionInfo.RawOutput})");
Console.WriteLine();

// Delete output directory
try
{
    Directory.Delete(outputDir, recursive: true);
    Directory.CreateDirectory(outputDir);
} catch
{
    // ignore
}

// Load all definitions (retry once after regenerating schema when default schema folder is stale/corrupt)
List<TypeDef> defs;
try
{
    defs = await SchemaWalker.LoadDefinitionsAsync(schemaFile, rootNamespace);
}
catch (Exception ex) when (schemaFile is not null && schemaFile.StartsWith(schemaDir, StringComparison.OrdinalIgnoreCase))
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Failed to load schema (will regenerate once): {ex.Message}");
    Console.ResetColor();

    try
    {
        if (Directory.Exists(schemaDir))
        {
            Directory.Delete(schemaDir, recursive: true);
        }
    }
    catch
    {
        // Best-effort cleanup; codex will overwrite/regen files anyway.
    }

    Console.WriteLine("Regenerating schema via: codex app-server generate-json-schema ...");

    Directory.CreateDirectory(schemaDir);

    var mixedCandidate = Path.Combine(schemaDir, mixedSchemaFileName);
    var v2Candidate = Path.Combine(schemaDir, flatV2SchemaFileName);
    SafeDelete(mixedCandidate);
    SafeDelete(v2Candidate);

    var experimentalFlag = includeExperimentalApi ? " --experimental" : "";
    var codexArgs = $"app-server generate-json-schema --out \"{schemaDir}\"{experimentalFlag}";
    ProcessStartInfo psi;
    if (codexPath != null)
    {
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            codexPath,
            codexArgs);
    }
    else if (OperatingSystem.IsWindows())
    {
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            "pwsh",
            $"-NoProfile -NonInteractive -Command \"codex {codexArgs.Replace('\"', '\'')}\"");
    }
    else
    {
        psi = CodexProcessHelper.CreateCommandProcessStartInfo(
            "/bin/sh",
            $"-c \"codex {codexArgs.Replace('\"', '\'')}\"");
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

    schemaFile = ResolveSchemaFile(
        mixedCandidate: mixedCandidate,
        v2Candidate: v2Candidate,
        schemaBundle: schemaBundle,
        includeExperimentalApi: includeExperimentalApi);

    defs = await SchemaWalker.LoadDefinitionsAsync(schemaFile, rootNamespace);
}
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
    // CodeAlta.CodexSdk.Sub -> outputDir/Sub
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

static void SafeDelete(string path)
{
    try
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
    catch
    {
        // ignore
    }
}

static string EnsureSchemaFile(string candidate, SchemaBundle bundle, bool includeExperimentalApi)
{
    if (File.Exists(candidate))
    {
        return candidate;
    }

    var experimentalHint = includeExperimentalApi
        ? ""
        : " If you're on an older codex build, try passing --experimental (it may be required to emit the flat v2 bundle).";

    throw new FileNotFoundException(
        $"Schema bundle '{bundle}' was requested but was not found at '{candidate}'.{experimentalHint}");
}

static string ResolveSchemaFile(
    string mixedCandidate,
    string v2Candidate,
    SchemaBundle schemaBundle,
    bool includeExperimentalApi)
{
    switch (schemaBundle)
    {
        case SchemaBundle.V2:
            if (File.Exists(v2Candidate))
            {
                return v2Candidate;
            }

            if (File.Exists(mixedCandidate))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"Warning: '{flatV2SchemaFileName}' was not generated; falling back to '{mixedSchemaFileName}'.");
                Console.ResetColor();
                return mixedCandidate;
            }

            _ = EnsureSchemaFile(v2Candidate, schemaBundle, includeExperimentalApi);
            _ = EnsureSchemaFile(mixedCandidate, schemaBundle, includeExperimentalApi);
            throw new InvalidOperationException("Unreachable");

        case SchemaBundle.Mixed:
            return EnsureSchemaFile(mixedCandidate, schemaBundle, includeExperimentalApi);

        case SchemaBundle.Auto:
            if (File.Exists(v2Candidate))
            {
                return v2Candidate;
            }

            return EnsureSchemaFile(mixedCandidate, schemaBundle, includeExperimentalApi);

        default:
            throw new ArgumentOutOfRangeException(nameof(schemaBundle), schemaBundle, null);
    }
}

static SchemaBundle ParseSchemaBundle(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "auto" => SchemaBundle.Auto,
        "mixed" or "schemas" => SchemaBundle.Mixed,
        "v2" or "flatv2" or "v2flat" => SchemaBundle.V2,
        _ => throw new ArgumentException($"Unknown --bundle value: '{value}'. Expected: auto | mixed | v2.")
    };
}

static string EscapeStringLiteral(string value)
{
    return value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
