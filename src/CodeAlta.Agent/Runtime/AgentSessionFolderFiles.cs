namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Says, by its name alone, which session a file of the sessions folder belongs to: its journal
/// (<c>&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/&lt;session&gt;.jsonl</c>), a file of its attachments folder (<c>&lt;session&gt;.attachments</c>)
/// or its protocol trace (<c>traces/&lt;session&gt;.trace</c>). The retention of sessions and the measure of their sizes read
/// the folder by these rules, the ones the store writes it by.
/// </summary>
internal static class AgentSessionFolderFiles
{
    /// <summary>Gets whether a file is in the folder of the leases or of the markers, whose owners remove them.</summary>
    /// <param name="root">The sessions folder.</param>
    /// <param name="path">A file under it.</param>
    internal static bool IsOwnedElsewhere(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return parts.Length > 1 && (string.Equals(parts[0], AgentRuntimePathLayout.SessionLeasesFolderName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parts[0], AgentRuntimePathLayout.SessionMarkersFolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Gets the session a file belongs to and what it is to it; no owner for a file of no session.</summary>
    /// <param name="root">The sessions folder.</param>
    /// <param name="path">A file under it.</param>
    internal static (string? Owner, SessionFolderFileKind Kind) OwnerOf(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var name = parts[^1];
        if (parts.Length == 2 && string.Equals(parts[0], "traces", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
            return (Owner(name[..^".trace".Length]), SessionFolderFileKind.Artifact);
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            if (part.EndsWith(".attachments", StringComparison.OrdinalIgnoreCase))
                return (Owner(part[..^".attachments".Length]), SessionFolderFileKind.Artifact);
        }

        return name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? (Owner(name[..^".jsonl".Length]), SessionFolderFileKind.Journal) : (null, SessionFolderFileKind.Other);

        static string? Owner(string id) => string.IsNullOrWhiteSpace(id) ? null : id;
    }
}
