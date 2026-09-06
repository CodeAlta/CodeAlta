using System.Globalization;
using System.Text;

namespace CodeAlta.Catalog.Skills;

// Deliberately limited to scaffolding new skills, not a general filesystem transaction layer.
internal static class SkillAuthoring
{
    internal static async Task<SkillCreationResult> CreateAsync(
        string globalRoot, string? projectRoot, SkillCreationTargetKind kind,
        string? name, string? description, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var normalizedName = NormalizeName(name, portable: true);
        var normalizedDescription = NormalizeDescription(description);
        var root = ValidateRoot(kind == SkillCreationTargetKind.ProjectCodeAlta ? projectRoot : globalRoot);
        var skillsRoot = kind == SkillCreationTargetKind.ProjectCodeAlta
            ? Path.Combine(root, ".alta", "skills") : Path.Combine(root, "skills");
        var finalRoot = Path.Combine(skillsRoot, normalizedName);
        ValidateComponents(finalRoot);
        if (Exists(finalRoot))
        {
            throw new InvalidOperationException($"Skill '{normalizedName}' already exists at '{finalRoot}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(skillsRoot);
        var stagingPath = Path.Combine(skillsRoot, $".skill-staging-{Guid.NewGuid():N}");
        if (Exists(stagingPath))
        {
            throw new IOException("Skill staging path already exists.");
        }

        var staging = Directory.CreateDirectory(stagingPath);
        var published = false;
        try
        {
            foreach (var folder in new[] { "scripts", "references", "assets" })
            {
                Directory.CreateDirectory(Path.Combine(staging.FullName, folder));
            }

            await File.WriteAllTextAsync(Path.Combine(staging.FullName, "SKILL.md"),
                BuildTemplate(normalizedName, normalizedDescription), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateComponents(finalRoot);
            // Directory.Move never merges with or replaces an existing destination.
            Directory.Move(staging.FullName, finalRoot);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(staging.FullName))
            {
                Directory.Delete(staging.FullName, recursive: true);
            }
        }

        return new SkillCreationResult(normalizedName, finalRoot, Path.Combine(finalRoot, "SKILL.md"), kind);
    }

    internal static string ValidateRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("An explicit root is required for this skill operation.");
        }

        if (!Path.IsPathFullyQualified(root) || root.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0 ||
            root.Split(['/', '\\']).Any(static part => part is "." or ".."))
        {
            throw new ArgumentException("Skill roots must be absolute paths without traversal or invalid path characters.", nameof(root));
        }

        var fullPath = Path.GetFullPath(root);
        var relative = fullPath[Path.GetPathRoot(fullPath)!.Length..];
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.IndexOfAny([':', '\\', '/', '<', '>', '"', '|', '?', '*']) >= 0 ||
                part.Any(char.IsControl) || part.EndsWith('.') || part.EndsWith(' ') ||
                IsDeviceName(part.Split('.')[0].ToLowerInvariant()))
            {
                throw new ArgumentException("Skill roots must use portable directory names.", nameof(root));
            }
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Skill root '{fullPath}' is unavailable.");
        }

        return fullPath;
    }

    private static bool Exists(string path)
        => new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null ||
           File.Exists(path) || Directory.Exists(path);

    private static void ValidateComponents(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null || new FileInfo(current.FullName).LinkTarget is not null ||
                (Exists(current.FullName) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0))
            {
                throw new IOException($"Linked or reparse creation paths are not supported: '{current.FullName}'.");
            }

            if (File.Exists(current.FullName))
            {
                throw new IOException($"A file occupies the creation path: '{current.FullName}'.");
            }
        }
    }

    internal static string NormalizeName(string? name, bool portable)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Skill name is required.", nameof(name));
        }

        var normalized = name.Trim();
        if (normalized.Length > 64)
        {
            throw new ArgumentException("Skill name must be 64 characters or fewer.", nameof(name));
        }

        if (normalized.StartsWith('-') || normalized.EndsWith('-') || normalized.Contains("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Skill name may not start or end with '-' and may not contain consecutive hyphens.", nameof(name));
        }

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (rune.Value != '-' && (!Rune.IsLetterOrDigit(rune) || (Rune.IsLetter(rune) && Rune.ToLowerInvariant(rune) != rune)))
            {
                throw new ArgumentException("Skill name must contain only lowercase Unicode alphanumeric characters and hyphens.", nameof(name));
            }
        }

        if (portable && IsDeviceName(normalized))
        {
            throw new ArgumentException("Skill name must not be a reserved portable device name.", nameof(name));
        }

        return normalized;
    }

    private static bool IsDeviceName(string name)
        => name is "con" or "prn" or "aux" or "nul" ||
           (name.Length == 4 && (name.StartsWith("com", StringComparison.Ordinal) || name.StartsWith("lpt", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(name[3]));

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Skill description is required.", nameof(description));
        }

        if (description.Any(static c => (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) || c is '\u0085' or '\u2028' or '\u2029'))
        {
            throw new ArgumentException("Skill description contains unsupported control or invalid Unicode characters.", nameof(description));
        }

        for (var i = 0; i < description.Length;)
        {
            if (!Rune.TryGetRuneAt(description, i, out var rune))
            {
                throw new ArgumentException("Skill description contains invalid Unicode characters.", nameof(description));
            }

            i += rune.Utf16SequenceLength;
        }

        var normalized = string.Join(' ', description.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 1024)
        {
            throw new ArgumentException("Skill description must be 1024 characters or fewer.", nameof(description));
        }

        return normalized;
    }

    private static string BuildTemplate(string name, string description)
    {
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('-', ' '));
        return
            $"""
            ---
            name: {name}
            description: '{description.Replace("'", "''", StringComparison.Ordinal)}'
            ---

            # {title}

            Use this skill when a task clearly matches this workflow.

            ## When to use

            - Describe the situations where this skill should be activated.

            ## Workflow

            1. Review the user's request and confirm this skill applies.
            2. Load any supporting files from `references/`, `scripts/`, or `assets/` only when needed.
            3. Follow normal CodeAlta approval and tool-use rules; do not execute scripts automatically.
            """;
    }
}
