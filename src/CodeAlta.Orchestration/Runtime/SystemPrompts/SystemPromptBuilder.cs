using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime.SystemPrompts;

/// <summary>
/// Builds file-backed CodeAlta system prompt bundles.
/// </summary>
public sealed class SystemPromptBuilder
{
    private const string DefaultName = "default";
    private static readonly string[] KnownTopLevelEntries = ["base", "instructions", "template.yml"];
    private readonly ISystemPromptContentLocator _contentLocator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemPromptBuilder"/> class.
    /// </summary>
    /// <param name="contentLocator">Content locator used to resolve prompt roots.</param>
    public SystemPromptBuilder(ISystemPromptContentLocator? contentLocator = null)
    {
        _contentLocator = contentLocator ?? new FileSystemPromptContentLocator();
    }

    /// <summary>
    /// Builds a system prompt bundle from file-backed resources and generated runtime parts.
    /// </summary>
    /// <param name="request">Build inputs.</param>
    /// <returns>The composed prompt bundle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when required prompt content is missing or invalid.</exception>
    public SystemPromptBundle Build(SystemPromptBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderKey);
        ArgumentNullException.ThrowIfNull(request.Thread);

        var diagnostics = new List<SystemPromptDiagnostic>();
        var projectRoot = NormalizeOptionalRoot(request.Project?.ProjectPath ?? FirstNonBlank(request.ProjectRoots));
        var roots = _contentLocator.GetRoots(new SystemPromptDiscoveryContext
        {
            ProjectRoot = projectRoot,
            ProjectPromptResourcesTrusted = projectRoot is not null,
        });

        ValidateRoots(roots, diagnostics);
        WarnForIgnoredFiles(roots, diagnostics);

        var template = ResolveTemplate(roots, request, diagnostics);
        var baseResolution = ResolveResource(roots, "base", ".system-prompt.md", template.BaseName, diagnostics);
        var instructionResolution = ResolveResource(roots, "instructions", ".instructions.md", template.InstructionName, diagnostics);

        if (baseResolution.Selected is null)
        {
            throw new InvalidOperationException($"Missing required base system prompt 'base/{template.BaseName}.system-prompt.md'.");
        }

        if (instructionResolution.Selected is null)
        {
            throw new InvalidOperationException($"Missing required thread instructions 'instructions/{template.InstructionName}.instructions.md'.");
        }

        var parts = new List<SystemPromptManifestPart>();
        var systemMessage = baseResolution.Selected.Body.Trim();
        parts.Add(CreateResourcePart(baseResolution.Selected, "base", template.BaseName, "system", 100, "selected", baseResolution.ReplacedPath));
        foreach (var skipped in baseResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "base", template.BaseName, "system", 100, skipped.Status, null));
        }

        var developerParts = new List<RenderedPromptPart>();
        AddDeveloperPart(developerParts, parts, CreateResourcePart(instructionResolution.Selected, "instruction", template.InstructionName, "developer", 300, "selected", instructionResolution.ReplacedPath), "Thread Instructions", instructionResolution.Selected.Body);
        foreach (var skipped in instructionResolution.Skipped)
        {
            parts.Add(CreateResourcePart(skipped.Resource, "instruction", template.InstructionName, "developer", 300, skipped.Status, null));
        }

        if (template.PartOptions.RuntimeContext)
        {
            AddGeneratedPart(developerParts, parts, "runtime.context", "runtime_context", "Runtime Context", 400, BuildRuntimeContext(request, projectRoot));
        }

        if (template.PartOptions.ToolGuidance)
        {
            AddGeneratedPart(developerParts, parts, "tool.guidance", "tool_guidance", "Tool Guidance", 500, BuildToolGuidance(request));
        }

        if (template.PartOptions.Skills && !string.IsNullOrWhiteSpace(request.AvailableSkillsMarkdown))
        {
            AddGeneratedPart(developerParts, parts, "skills.available", "available_skills", "Available Skills", 550, request.AvailableSkillsMarkdown!);
        }

        if (template.PartOptions.Skills && !string.IsNullOrWhiteSpace(request.ActiveSkillsMarkdown))
        {
            AddGeneratedPart(developerParts, parts, "skills.active", "active_skills", "Active Skills", 575, request.ActiveSkillsMarkdown!);
        }

        if (template.PartOptions.ProjectContext)
        {
            var projectContext = BuildProjectContext(request, projectRoot, diagnostics, out var projectContextFiles);
            if (!string.IsNullOrWhiteSpace(projectContext))
            {
                var projectPart = AddGeneratedPart(developerParts, parts, "project.context", "project_context", "Project Context", 600, projectContext);
                projectPart = projectPart with { SourcePaths = projectContextFiles };
                parts[^1] = projectPart;
            }
        }

        var developerInstructions = developerParts.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, developerParts.Select(static part => part.Markdown.Trim()));
        var statistics = SystemPromptStatistics.FromMessages(systemMessage, developerInstructions, parts);
        var effectiveHash = ComputeHash(systemMessage, developerInstructions, request.ProviderKey, request.ProviderType, request.ProtocolFamily, request.Model, "native-system-and-developer");
        var provider = new SystemPromptProviderMapping(
            request.ProviderKey,
            request.ProviderType,
            request.ProtocolFamily,
            request.Model,
            "native-system-and-developer",
            AppliedToProvider: true,
            Lossy: false,
            Notes: []);
        var manifest = new SystemPromptManifest(
            Version: 1,
            PromptId: CreatePromptId(effectiveHash),
            EffectivePromptHash: effectiveHash,
            Template: template.ToManifest(),
            Provider: provider,
            Parts: parts,
            Statistics: statistics,
            Diagnostics: diagnostics);

        return new SystemPromptBundle
        {
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            EffectivePromptHash = effectiveHash,
            Manifest = manifest,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static void ValidateRoots(SystemPromptContentRoots roots, List<SystemPromptDiagnostic> diagnostics)
    {
        if (!Directory.Exists(roots.ShippedPromptRoot))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_content_root", $"Shipped prompt root '{roots.ShippedPromptRoot}' was not found.", roots.ShippedPromptRoot));
            throw new InvalidOperationException($"Shipped prompt root '{roots.ShippedPromptRoot}' was not found.");
        }

        var basePath = Path.Combine(roots.ShippedPromptRoot, "base", "default.system-prompt.md");
        if (!File.Exists(basePath))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_base", $"Required shipped base system prompt '{basePath}' was not found.", basePath));
            throw new InvalidOperationException($"Required shipped base system prompt '{basePath}' was not found.");
        }

        var instructionPath = Path.Combine(roots.ShippedPromptRoot, "instructions", "default.instructions.md");
        if (!File.Exists(instructionPath))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_shipped_instructions", $"Required shipped default thread instructions '{instructionPath}' were not found.", instructionPath));
            throw new InvalidOperationException($"Required shipped default thread instructions '{instructionPath}' were not found.");
        }
    }

    private static void WarnForIgnoredFiles(SystemPromptContentRoots roots, List<SystemPromptDiagnostic> diagnostics)
    {
        foreach (var root in EnumerateExistingRoots(roots))
        {
            foreach (var directory in Directory.EnumerateDirectories(root.Path))
            {
                var name = Path.GetFileName(directory);
                if (!string.Equals(name, "base", StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "instructions", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_unknown_prompt_folder", $"Ignoring unknown prompt resource folder '{directory}'.", directory));
                }
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(root.Path))
            {
                if (KnownTopLevelEntries.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(entry))
                {
                    diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_unknown_prompt_file", $"Ignoring unknown prompt resource file '{entry}'.", entry));
                }
            }

            WarnWrongSuffix(root.Path, "base", ".system-prompt.md", diagnostics);
            WarnWrongSuffix(root.Path, "instructions", ".instructions.md", diagnostics);
        }
    }

    private static void WarnWrongSuffix(string root, string folder, string suffix, List<SystemPromptDiagnostic> diagnostics)
    {
        var directory = Path.Combine(root, folder);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("ignored_wrong_suffix", $"Ignoring prompt resource file with wrong suffix '{file}'.", file));
            }
        }
    }

    private static SystemPromptResolvedTemplate ResolveTemplate(SystemPromptContentRoots roots, SystemPromptBuildRequest request, List<SystemPromptDiagnostic> diagnostics)
    {
        var result = new SystemPromptResolvedTemplate(DefaultName, "default-name", DefaultName, "default-name", SystemPromptPartOptions.Default, []);
        foreach (var templateSource in EnumerateTemplateSources(roots))
        {
            if (!File.Exists(templateSource.Path))
            {
                continue;
            }

            var parsed = ParseTemplateFile(templateSource.Path, diagnostics);
            var files = result.TemplateFiles.Concat([new SystemPromptTemplateFileManifest(templateSource.SourceKind, templateSource.Path, HashFile(templateSource.Path), parsed.HasErrors ? "error" : "loaded")]).ToArray();
            if (parsed.HasErrors)
            {
                result = result with { TemplateFiles = files };
                continue;
            }

            result = new SystemPromptResolvedTemplate(
                parsed.BaseName ?? result.BaseName,
                parsed.BaseName is null ? result.BaseReason : $"template:{templateSource.SourceKind}",
                parsed.InstructionName ?? result.InstructionName,
                parsed.InstructionName is null ? result.InstructionReason : $"template:{templateSource.SourceKind}",
                parsed.Options.MergeOver(result.PartOptions),
                files);
        }

        if (!string.IsNullOrWhiteSpace(request.SelectedBaseName))
        {
            result = result with { BaseName = request.SelectedBaseName.Trim(), BaseReason = "runtime" };
        }

        if (!string.IsNullOrWhiteSpace(request.SelectedInstructionName))
        {
            result = result with { InstructionName = request.SelectedInstructionName.Trim(), InstructionReason = "runtime" };
        }

        result = result with { PartOptions = request.PartOptionsOverride?.MergeOver(result.PartOptions) ?? result.PartOptions };
        return result;
    }

    private static ParsedTemplate ParseTemplateFile(string path, List<SystemPromptDiagnostic> diagnostics)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("unreadable_prompt_template", $"Prompt template '{path}' could not be read: {ex.Message}", path));
            return ParsedTemplate.Error;
        }

        var values = ParseFlatKeyValueFile(text, diagnostics, path, allowFrontmatterDelimiters: false);
        var hasErrors = diagnostics.Any(d => d.Severity == SystemPromptDiagnosticSeverity.Error && string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        var options = PartialSystemPromptPartOptions.Empty;
        foreach (var key in values.Keys)
        {
            switch (key)
            {
                case "version":
                    if (!int.TryParse(values[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version != 1)
                    {
                        diagnostics.Add(SystemPromptDiagnostic.Error("invalid_prompt_template", $"Prompt template '{path}' has unsupported version '{values[key]}'.", path));
                        hasErrors = true;
                    }

                    break;
                case "base":
                case "instruction":
                    break;
                case "skills":
                    options = options with { Skills = ParseBool(values[key], key, path, diagnostics, ref hasErrors) };
                    break;
                case "project_context":
                    options = options with { ProjectContext = ParseBool(values[key], key, path, diagnostics, ref hasErrors) };
                    break;
                case "runtime_context":
                    options = options with { RuntimeContext = ParseBool(values[key], key, path, diagnostics, ref hasErrors) };
                    break;
                case "tool_guidance":
                    options = options with { ToolGuidance = ParseBool(values[key], key, path, diagnostics, ref hasErrors) };
                    break;
                default:
                    diagnostics.Add(SystemPromptDiagnostic.Warning("unknown_prompt_template_field", $"Prompt template '{path}' contains unknown field '{key}'.", path));
                    break;
            }
        }

        return new ParsedTemplate(
            NormalizeName(values.GetValueOrDefault("base")),
            NormalizeName(values.GetValueOrDefault("instruction")),
            options,
            hasErrors);
    }

    private static bool? ParseBool(string value, string field, string path, List<SystemPromptDiagnostic> diagnostics, ref bool hasErrors)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        diagnostics.Add(SystemPromptDiagnostic.Error("invalid_prompt_template", $"Prompt template '{path}' field '{field}' must be true or false.", path));
        hasErrors = true;
        return null;
    }

    private static ResourceResolution ResolveResource(SystemPromptContentRoots roots, string folder, string suffix, string name, List<SystemPromptDiagnostic> diagnostics)
    {
        var candidates = EnumerateExistingRoots(roots)
            .Select(root => LoadResource(root, folder, suffix, name, diagnostics))
            .Where(static resource => resource is not null)
            .Cast<PromptResource>()
            .OrderBy(static resource => resource.Precedence)
            .ToArray();
        if (candidates.Length == 0)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("missing_prompt_resource", $"Prompt resource '{folder}/{name}{suffix}' was not found.", null));
            return new ResourceResolution(null, null, []);
        }

        var selected = candidates[^1];
        var replaced = candidates.Length > 1 ? candidates[^2].Path : null;
        if (!string.Equals(selected.SourceKind, "built-in", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(SystemPromptDiagnostic.Warning($"active_{folder}_override", $"Active {folder} resource comes from {selected.SourceKind}: {selected.Path}", selected.Path));
        }

        var skipped = candidates.Take(candidates.Length - 1)
            .Select(static resource => new SkippedResource(resource, "replaced"))
            .ToArray();
        return new ResourceResolution(selected, replaced, skipped);
    }

    private static PromptResource? LoadResource(PromptRoot root, string folder, string suffix, string name, List<SystemPromptDiagnostic> diagnostics)
    {
        var path = Path.Combine(root.Path, folder, name + suffix);
        if (!File.Exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("unreadable_prompt_resource", $"Prompt resource '{path}' could not be read: {ex.Message}", path));
            return null;
        }

        var (frontmatter, body) = SplitFrontmatter(text, path, diagnostics);
        foreach (var key in frontmatter.Keys)
        {
            if (key is not ("description" or "version" or "max_tokens" or "id" or "kind" or "path"))
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("unknown_frontmatter_field", $"Prompt resource '{path}' contains unknown frontmatter field '{key}'.", path));
            }
            else if (key is "id" or "kind" or "path")
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("derived_frontmatter_field", $"Prompt resource '{path}' frontmatter field '{key}' is ignored because kind/name/path are derived from the file location.", path));
            }
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("empty_prompt_resource", $"Prompt resource '{path}' is empty.", path));
        }

        return new PromptResource(root.SourceKind, root.Precedence, path, body.Trim(), frontmatter.GetValueOrDefault("description"), HashText(body.Trim()));
    }

    private static Dictionary<string, string> ParseFlatKeyValueFile(string text, List<SystemPromptDiagnostic> diagnostics, string path, bool allowFrontmatterDelimiters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (allowFrontmatterDelimiters && line == "---")
            {
                continue;
            }

            var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0)
            {
                diagnostics.Add(SystemPromptDiagnostic.Error("invalid_yaml", $"File '{path}' has invalid key/value syntax on line {lineNumber}.", path));
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }

        return values;
    }

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text, string path, List<SystemPromptDiagnostic> diagnostics)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            diagnostics.Add(SystemPromptDiagnostic.Error("invalid_frontmatter", $"Prompt resource '{path}' has unterminated frontmatter.", path));
            return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);
        }

        var frontmatterText = normalized[4..end];
        var body = normalized[(end + 5)..];
        return (ParseFlatKeyValueFile(frontmatterText, diagnostics, path, allowFrontmatterDelimiters: true), body);
    }

    private static SystemPromptManifestPart CreateResourcePart(PromptResource resource, string kind, string name, string target, int order, string status, string? replaces)
        => new(
            Key: $"{(kind == "base" ? "base" : "instructions")}/{name}",
            Kind: kind,
            Name: name,
            Target: target,
            Order: order,
            SourceKind: resource.SourceKind,
            Path: resource.Path,
            Hash: resource.Hash,
            ApproxTokens: EstimateTokens(resource.Body),
            Status: status,
            Replaces: replaces,
            SourcePaths: []);

    private static void AddDeveloperPart(List<RenderedPromptPart> developerParts, List<SystemPromptManifestPart> manifestParts, SystemPromptManifestPart manifestPart, string title, string body)
    {
        manifestParts.Add(manifestPart);
        developerParts.Add(new RenderedPromptPart(manifestPart.Key, RenderSection(title, body)));
    }

    private static SystemPromptManifestPart AddGeneratedPart(List<RenderedPromptPart> developerParts, List<SystemPromptManifestPart> manifestParts, string key, string kind, string title, int order, string body)
    {
        var trimmed = body.Trim();
        var part = new SystemPromptManifestPart(
            Key: key,
            Kind: kind,
            Name: null,
            Target: "developer",
            Order: order,
            SourceKind: "generated",
            Path: null,
            Hash: HashText(trimmed),
            ApproxTokens: EstimateTokens(trimmed),
            Status: "selected",
            Replaces: null,
            SourcePaths: []);
        manifestParts.Add(part);
        developerParts.Add(new RenderedPromptPart(key, RenderSection(title, trimmed)));
        return part;
    }

    private static string BuildRuntimeContext(SystemPromptBuildRequest request, string? projectRoot)
    {
        var lines = new List<string>
        {
            $"- Current date: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"- Platform: {GetPlatformLabel()}",
            $"- Default shell for shell commands: {GetDefaultShellLabel()}",
        };
        var workingDirectory = NormalizeOptionalRoot(request.Thread.WorkingDirectory) ?? NormalizeOptionalRoot(request.WorkingDirectory);
        if (workingDirectory is not null)
        {
            lines.Add($"- Current working directory: {workingDirectory}");
        }

        if (projectRoot is not null)
        {
            lines.Add($"- Project root: {projectRoot}");
        }

        if (!string.IsNullOrWhiteSpace(request.Thread.ParentThreadId))
        {
            lines.Add($"- Parent thread: {request.Thread.ParentThreadId}");
        }

        lines.Add($"- Thread kind: {request.Thread.Kind.ToString().ToLowerInvariant()}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildToolGuidance(SystemPromptBuildRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Tool schemas and handlers are supplied separately by the host. Use only tools that are actually available in the current runtime.");
        builder.AppendLine("Treat tool results as evidence, do not fabricate tool output, and respect host permission or approval prompts before acting.");
        if (request.Tools.Count > 0)
        {
            builder.Append("Available host tool count: ").Append(request.Tools.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }

        if (request.Tools.Any(static tool => string.Equals(tool.Spec.Name, "alta", StringComparison.OrdinalIgnoreCase)))
        {
            builder.AppendLine("Use the `alta` live tool for finite CodeAlta host/session/catalog operations. Start with args [\"--help\"], then narrower help such as [\"session\",\"--help\"] before invoking mutating commands.");
        }

        return builder.ToString().Trim();
    }

    private static string? BuildProjectContext(SystemPromptBuildRequest request, string? projectRoot, List<SystemPromptDiagnostic> diagnostics, out IReadOnlyList<string> files)
    {
        var selectedFiles = EnumerateProjectInstructionFiles(request.Thread.WorkingDirectory ?? request.WorkingDirectory, request.ProjectRoots.Count > 0 ? request.ProjectRoots : projectRoot is null ? [] : [projectRoot]);
        files = selectedFiles;
        if (selectedFiles.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var path in selectedFiles)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > 64 * 1024)
            {
                diagnostics.Add(SystemPromptDiagnostic.Warning("large_project_context_file", $"Project instruction/context file '{path}' is unusually large.", path));
            }

            var content = File.ReadAllText(path).Trim();
            if (content.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.AppendLine($"File: {path}");
            builder.AppendLine();
            builder.AppendLine("<INSTRUCTIONS>");
            builder.AppendLine();
            builder.AppendLine(content);
            builder.AppendLine();
            builder.Append("</INSTRUCTIONS>");
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static IReadOnlyList<string> EnumerateProjectInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateRelativePaths = new[]
        {
            "AGENTS.md",
            "CLAUDE.md",
            Path.Combine(".github", "copilot-instructions.md"),
        };

        void AddWalk(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            var current = Path.GetFullPath(root);
            var stack = new Stack<string>();
            while (!string.IsNullOrWhiteSpace(current))
            {
                stack.Push(current);
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }

            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                var selectedFile = candidateRelativePaths
                    .Select(relativePath => Path.Combine(directory, relativePath))
                    .Where(File.Exists)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(static file => file.Length)
                    .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (selectedFile is not null && seen.Add(selectedFile.FullName))
                {
                    files.Add(selectedFile.FullName);
                }
            }
        }

        AddWalk(workingDirectory);
        foreach (var projectRoot in projectRoots)
        {
            AddWalk(projectRoot);
        }

        return files;
    }

    private static IEnumerable<PromptRoot> EnumerateExistingRoots(SystemPromptContentRoots roots)
    {
        if (Directory.Exists(roots.ShippedPromptRoot))
        {
            yield return new PromptRoot("built-in", 0, roots.ShippedPromptRoot);
        }

        if (Directory.Exists(roots.UserPromptRoot))
        {
            yield return new PromptRoot("user-global", 1, roots.UserPromptRoot);
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && Directory.Exists(roots.ProjectPromptRoot))
        {
            yield return new PromptRoot("project", 2, roots.ProjectPromptRoot);
        }
    }

    private static IEnumerable<TemplateSource> EnumerateTemplateSources(SystemPromptContentRoots roots)
    {
        if (File.Exists(Path.Combine(roots.UserPromptRoot, "template.yml")))
        {
            yield return new TemplateSource("user-global", Path.Combine(roots.UserPromptRoot, "template.yml"));
        }

        if (roots.ProjectPromptResourcesTrusted && roots.ProjectPromptRoot is not null && File.Exists(Path.Combine(roots.ProjectPromptRoot, "template.yml")))
        {
            yield return new TemplateSource("project", Path.Combine(roots.ProjectPromptRoot, "template.yml"));
        }
    }

    private static string RenderSection(string title, string body)
        => $"# {title}{Environment.NewLine}{Environment.NewLine}{body.Trim()}";

    private static string HashFile(string path)
        => HashText(File.ReadAllText(path));

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(bytes);
    }

    private static string ComputeHash(string? systemMessage, string? developerInstructions, string providerKey, string? providerType, string? protocolFamily, string? model, string channelMapping)
        => HashText(string.Join("\n---\n", systemMessage ?? string.Empty, developerInstructions ?? string.Empty, providerKey, providerType ?? string.Empty, protocolFamily ?? string.Empty, model ?? string.Empty, channelMapping));

    private static string CreatePromptId(string hash)
        => "sp_" + hash.Replace("sha256:", string.Empty, StringComparison.Ordinal).ToLowerInvariant()[..16];

    private static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    private static string? NormalizeOptionalRoot(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? NormalizeName(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private static string? FirstNonBlank(IEnumerable<string> values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return RuntimeInformation.OSDescription.Trim();
    }

    private static string GetDefaultShellLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(shell) ? "/bin/sh" : shell.Trim();
    }

    private sealed record PromptRoot(string SourceKind, int Precedence, string Path);
    private sealed record TemplateSource(string SourceKind, string Path);
    private sealed record PromptResource(string SourceKind, int Precedence, string Path, string Body, string? Description, string Hash);
    private sealed record ResourceResolution(PromptResource? Selected, string? ReplacedPath, IReadOnlyList<SkippedResource> Skipped);
    private sealed record SkippedResource(PromptResource Resource, string Status);
    private sealed record RenderedPromptPart(string Key, string Markdown);
    private sealed record ParsedTemplate(string? BaseName, string? InstructionName, PartialSystemPromptPartOptions Options, bool HasErrors)
    {
        public static ParsedTemplate Error { get; } = new(null, null, PartialSystemPromptPartOptions.Empty, true);
    }

    private sealed record SystemPromptResolvedTemplate(string BaseName, string BaseReason, string InstructionName, string InstructionReason, SystemPromptPartOptions PartOptions, IReadOnlyList<SystemPromptTemplateFileManifest> TemplateFiles)
    {
        public SystemPromptTemplateManifest ToManifest()
            => new(BaseName, BaseReason, InstructionName, InstructionReason, PartOptions, TemplateFiles);
    }
}

/// <summary>
/// Inputs used to build a CodeAlta system prompt.
/// </summary>
public sealed class SystemPromptBuildRequest
{
    /// <summary>Gets the provider key used by the active session.</summary>
    public required string ProviderKey { get; init; }

    /// <summary>Gets the optional provider type.</summary>
    public string? ProviderType { get; init; }

    /// <summary>Gets the optional protocol family.</summary>
    public string? ProtocolFamily { get; init; }

    /// <summary>Gets the selected model id.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the active work thread.</summary>
    public required WorkThreadDescriptor Thread { get; init; }

    /// <summary>Gets the owning project, if any.</summary>
    public ProjectDescriptor? Project { get; init; }

    /// <summary>Gets an optional selected base name override.</summary>
    public string? SelectedBaseName { get; init; }

    /// <summary>Gets an optional selected thread instruction name override.</summary>
    public string? SelectedInstructionName { get; init; }

    /// <summary>Gets optional prompt part overrides.</summary>
    public PartialSystemPromptPartOptions? PartOptionsOverride { get; init; }

    /// <summary>Gets the session working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets configured project roots.</summary>
    public IReadOnlyList<string> ProjectRoots { get; init; } = [];

    /// <summary>Gets enabled tool definitions.</summary>
    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = [];

    /// <summary>Gets available-skills markdown produced by the skill catalog.</summary>
    public string? AvailableSkillsMarkdown { get; init; }

    /// <summary>Gets active-skills markdown restored from session state.</summary>
    public string? ActiveSkillsMarkdown { get; init; }
}

/// <summary>
/// Boolean controls for generated system prompt parts.
/// </summary>
/// <param name="Skills">Whether skills parts are included.</param>
/// <param name="ProjectContext">Whether project instruction/context files are included.</param>
/// <param name="RuntimeContext">Whether runtime facts are included.</param>
/// <param name="ToolGuidance">Whether generated tool-use guidance is included.</param>
public sealed record SystemPromptPartOptions(bool Skills, bool ProjectContext, bool RuntimeContext, bool ToolGuidance)
{
    /// <summary>Gets default prompt part options.</summary>
    public static SystemPromptPartOptions Default { get; } = new(true, true, true, true);
}

/// <summary>
/// Partial prompt part option override set.
/// </summary>
/// <param name="Skills">Optional skills toggle.</param>
/// <param name="ProjectContext">Optional project context toggle.</param>
/// <param name="RuntimeContext">Optional runtime context toggle.</param>
/// <param name="ToolGuidance">Optional tool guidance toggle.</param>
public sealed record PartialSystemPromptPartOptions(bool? Skills, bool? ProjectContext, bool? RuntimeContext, bool? ToolGuidance)
{
    /// <summary>Gets an empty partial option set.</summary>
    public static PartialSystemPromptPartOptions Empty { get; } = new(null, null, null, null);

    /// <summary>Merges this partial override over lower-precedence options.</summary>
    /// <param name="lower">Lower-precedence options.</param>
    /// <returns>The merged options.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="lower"/> is <see langword="null"/>.</exception>
    public SystemPromptPartOptions MergeOver(SystemPromptPartOptions lower)
    {
        ArgumentNullException.ThrowIfNull(lower);
        return new SystemPromptPartOptions(
            Skills ?? lower.Skills,
            ProjectContext ?? lower.ProjectContext,
            RuntimeContext ?? lower.RuntimeContext,
            ToolGuidance ?? lower.ToolGuidance);
    }
}

/// <summary>
/// Composed prompt bundle and build receipt.
/// </summary>
public sealed class SystemPromptBundle
{
    /// <summary>Gets the logical system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the logical developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets the stable effective prompt hash.</summary>
    public required string EffectivePromptHash { get; init; }

    /// <summary>Gets the prompt manifest.</summary>
    public required SystemPromptManifest Manifest { get; init; }

    /// <summary>Gets prompt statistics.</summary>
    public required SystemPromptStatistics Statistics { get; init; }

    /// <summary>Gets build diagnostics.</summary>
    public IReadOnlyList<SystemPromptDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Structured manifest describing a composed prompt.
/// </summary>
public sealed record SystemPromptManifest(
    int Version,
    string PromptId,
    string EffectivePromptHash,
    SystemPromptTemplateManifest Template,
    SystemPromptProviderMapping Provider,
    IReadOnlyList<SystemPromptManifestPart> Parts,
    SystemPromptStatistics Statistics,
    IReadOnlyList<SystemPromptDiagnostic> Diagnostics);

/// <summary>
/// Prompt template selection metadata.
/// </summary>
public sealed record SystemPromptTemplateManifest(
    string BaseName,
    string BaseReason,
    string InstructionName,
    string InstructionReason,
    SystemPromptPartOptions PartOptions,
    IReadOnlyList<SystemPromptTemplateFileManifest> TemplateFiles);

/// <summary>
/// A template file used during prompt composition.
/// </summary>
public sealed record SystemPromptTemplateFileManifest(string SourceKind, string Path, string Hash, string Status);

/// <summary>
/// Provider mapping metadata for prompt audit.
/// </summary>
public sealed record SystemPromptProviderMapping(
    string ProviderKey,
    string? ProviderType,
    string? ProtocolFamily,
    string? Model,
    string ChannelMapping,
    bool AppliedToProvider,
    bool Lossy,
    IReadOnlyList<string> Notes);

/// <summary>
/// Manifest entry for one prompt part.
/// </summary>
public sealed record SystemPromptManifestPart(
    string Key,
    string Kind,
    string? Name,
    string Target,
    int Order,
    string SourceKind,
    string? Path,
    string Hash,
    int ApproxTokens,
    string Status,
    string? Replaces,
    IReadOnlyList<string> SourcePaths);

/// <summary>
/// Approximate prompt size statistics.
/// </summary>
public sealed record SystemPromptStatistics(int SystemApproxTokens, int DeveloperApproxTokens, int TotalApproxTokens, int SystemChars, int DeveloperChars, int PartCount)
{
    /// <summary>Creates statistics from logical messages and manifest parts.</summary>
    /// <param name="systemMessage">System message.</param>
    /// <param name="developerInstructions">Developer instructions.</param>
    /// <param name="parts">Manifest parts.</param>
    /// <returns>Prompt statistics.</returns>
    public static SystemPromptStatistics FromMessages(string? systemMessage, string? developerInstructions, IReadOnlyList<SystemPromptManifestPart> parts)
    {
        var systemChars = systemMessage?.Length ?? 0;
        var developerChars = developerInstructions?.Length ?? 0;
        var systemTokens = string.IsNullOrEmpty(systemMessage) ? 0 : Math.Max(1, (int)Math.Ceiling(systemChars / 4.0));
        var developerTokens = string.IsNullOrEmpty(developerInstructions) ? 0 : Math.Max(1, (int)Math.Ceiling(developerChars / 4.0));
        return new SystemPromptStatistics(systemTokens, developerTokens, systemTokens + developerTokens, systemChars, developerChars, parts.Count);
    }
}

/// <summary>
/// Prompt diagnostic severity.
/// </summary>
public enum SystemPromptDiagnosticSeverity
{
    /// <summary>An informational diagnostic.</summary>
    Info,

    /// <summary>A warning diagnostic.</summary>
    Warning,

    /// <summary>An error diagnostic.</summary>
    Error,
}

/// <summary>
/// Prompt build diagnostic.
/// </summary>
public sealed record SystemPromptDiagnostic(SystemPromptDiagnosticSeverity Severity, string Code, string Message, string? Path)
{
    /// <summary>Creates a warning diagnostic.</summary>
    public static SystemPromptDiagnostic Warning(string code, string message, string? path)
        => new(SystemPromptDiagnosticSeverity.Warning, code, message, path);

    /// <summary>Creates an error diagnostic.</summary>
    public static SystemPromptDiagnostic Error(string code, string message, string? path)
        => new(SystemPromptDiagnosticSeverity.Error, code, message, path);
}
