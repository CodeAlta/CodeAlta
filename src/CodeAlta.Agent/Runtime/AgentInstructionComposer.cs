using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Agent.Runtime;

internal static class AgentInstructionComposer
{
    public static AgentInstructionBundle Compose(
        AgentSessionCreateOptions options,
        IReadOnlyList<AgentLoadedSkillState>? loadedSkills = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var systemMessage = Normalize(options.SystemMessage);
        var developerInstructionsInput = Normalize(options.DeveloperInstructions);
        var runtimeContext = ContainsSection(developerInstructionsInput, "# Runtime Context")
            ? string.Empty
            : BuildRuntimeContextSection(options.WorkingDirectory, options.ProjectRoots);
        var developerSections = new List<string>();
        if (!string.IsNullOrWhiteSpace(developerInstructionsInput))
        {
            developerSections.Add(developerInstructionsInput);
        }

        if (!ContainsSection(developerInstructionsInput, "# Project Context"))
        {
            foreach (var path in EnumerateAgentInstructionFiles(options.WorkingDirectory, options.ProjectRoots))
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length == 0)
                {
                    continue;
                }

                developerSections.Add(
                    $"""
                    File: {path}
                    {content}
                    """);
            }
        }

        if (!ContainsSection(developerInstructionsInput, "# Active Skills") &&
            !ContainsSection(developerInstructionsInput, "<active_skills>"))
        {
            var activeSkillsSection = BuildActiveSkillsSection(loadedSkills);
            if (!string.IsNullOrWhiteSpace(activeSkillsSection))
            {
                developerSections.Add(activeSkillsSection);
            }
        }

        var developerInstructions = developerSections.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, developerSections);
        var hash = ComputeHash(systemMessage, developerInstructions, runtimeContext);
        return new AgentInstructionBundle(systemMessage, developerInstructions, runtimeContext, hash);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ContainsSection(string? value, string marker)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static string BuildRuntimeContextSection(string? workingDirectory, IReadOnlyList<string> projectRoots)
    {
        var lines = new List<string>
        {
            $"Current date: {DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            $"Platform: {GetPlatformLabel()}",
            $"Default shell for `shell_command`: `{GetDefaultShellLabel()}`",
        };

        var normalizedWorkingDirectory = NormalizePath(workingDirectory);
        if (normalizedWorkingDirectory is not null)
        {
            lines.Add($"Current working directory: `{normalizedWorkingDirectory}`");
        }

        var normalizedProjectRoots = projectRoots
            .Select(NormalizePath)
            .Where(static path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        if (normalizedProjectRoots.Length > 0)
        {
            lines.Add(
                normalizedProjectRoots.Length == 1
                    ? $"Project root: `{normalizedProjectRoots[0]}`"
                    : $"Project roots: {string.Join(", ", normalizedProjectRoots.Select(static root => $"`{root}`"))}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> EnumerateAgentInstructionFiles(string? workingDirectory, IReadOnlyList<string> projectRoots)
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

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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
        return string.IsNullOrWhiteSpace(shell)
            ? "/bin/sh"
            : shell.Trim();
    }

    private static string ComputeHash(string? systemMessage, string? developerInstructions, string? runtimeContext)
    {
        var payload = $"{systemMessage ?? string.Empty}\n---\n{developerInstructions ?? string.Empty}\n---\n{runtimeContext ?? string.Empty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

    private static string? BuildActiveSkillsSection(IReadOnlyList<AgentLoadedSkillState>? loadedSkills)
    {
        if (loadedSkills is not { Count: > 0 })
        {
            return null;
        }

        var orderedSkills = loadedSkills
            .OrderBy(static skill => skill.ActivatedAt)
            .ThenBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("The following skills are already active in this session. Treat them as loaded host-managed context.");
        builder.AppendLine("Use the skill root when resolving relative paths mentioned by a loaded skill.");
        builder.AppendLine();
        builder.AppendLine("<active_skills>");

        foreach (var skill in orderedSkills)
        {
            if (!skill.IsAvailable)
            {
                builder.Append("  <skill_missing name=\"")
                    .Append(EscapeXml(skill.Name))
                    .Append("\" path=\"")
                    .Append(EscapeXml(skill.SkillFilePath))
                    .Append("\">")
                    .Append(EscapeXml(skill.MissingReason ?? "Skill content was restored from session history but the on-disk skill is no longer available."))
                    .AppendLine("</skill_missing>");
            }

            builder.AppendLine(skill.Payload.Trim());
        }

        builder.AppendLine("</active_skills>");
        return builder.ToString().Trim();
    }

    private static string EscapeXml(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}

internal sealed record AgentInstructionBundle(
    string? SystemMessage,
    string? DeveloperInstructions,
    string RuntimeContext,
    string InstructionHash);
