namespace CodeAlta.Catalog;

/// <summary>
/// An archive policy of sessions, as <c>[[sessions.archive_policies]]</c> writes it: the sessions it applies to (those of
/// some labels, or the runs of some automations) and when they are archived (once inactive for some days, beyond the most
/// recent ones, or both). A session is archived, never deleted.
/// </summary>
/// <param name="Labels">The labels for sessions whose sessions it applies to, by their id; a label under one of them counts too.</param>
/// <param name="Automations">The automations whose runs it applies to, by their id or their name.</param>
/// <param name="OlderThan">How long a session stays inactive before it is archived; null for no age.</param>
/// <param name="KeepLast">How many of its sessions, the most recently active, are never archived; null for no such number.</param>
public sealed record SessionArchivePolicy(IReadOnlyList<string> Labels, IReadOnlyList<string> Automations, TimeSpan? OlderThan, int? KeepLast)
{
    /// <summary>The longest age a policy names, in days: ten years.</summary>
    public const int MaximumDays = 3650;

    /// <summary>The most recent sessions a policy keeps at most.</summary>
    public const int MaximumKeepLast = 10_000;

    /// <summary>The most policies one configuration file holds; the others are not read.</summary>
    public const int MaximumPolicies = 64;

    /// <summary>
    /// Reads a policy of a configuration file. A policy that names no label and no automation, or neither an age nor a
    /// number of sessions to keep, would archive what nobody chose: it is not read.
    /// </summary>
    /// <param name="document">The policy as the file writes it.</param>
    /// <param name="policy">The policy read, or <see langword="null"/>.</param>
    /// <param name="problem">What is wrong with it, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the policy can be applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryRead(CodeAltaSessionArchivePolicyDocument document, out SessionArchivePolicy? policy, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(document);
        policy = null;
        var labels = LabelDescriptor.NormalizeIds(document.Labels?.Select(static label => label?.ToLowerInvariant()));
        var automations = (document.Automations ?? []).Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (labels.Count == 0 && automations.Length == 0)
        {
            problem = "An archive policy names the labels (labels = [...]) or the automations (automations = [...]) of the sessions it archives.";
            return false;
        }

        if (document.OlderThanDays is null && document.KeepLast is null)
        {
            problem = "An archive policy says when a session is archived: older_than_days, keep_last, or both.";
            return false;
        }

        if (document.OlderThanDays is { } days && days is < 1 or > MaximumDays)
        {
            problem = $"older_than_days is a number of days from 1 to {MaximumDays}.";
            return false;
        }

        if (document.KeepLast is { } keep && keep is < 1 or > MaximumKeepLast)
        {
            problem = $"keep_last is a number of sessions from 1 to {MaximumKeepLast}.";
            return false;
        }

        problem = null;
        policy = new SessionArchivePolicy(labels, automations, document.OlderThanDays is { } age ? TimeSpan.FromDays(age) : null, document.KeepLast);
        return true;
    }

    /// <summary>Reads the policies of a configuration file: those that can be applied, the first <see cref="MaximumPolicies"/>.</summary>
    /// <param name="documents">The policies as the file writes them; <see langword="null"/> for none.</param>
    /// <returns>The policies read.</returns>
    public static IReadOnlyList<SessionArchivePolicy> ReadAll(IEnumerable<CodeAltaSessionArchivePolicyDocument?>? documents)
    {
        var policies = new List<SessionArchivePolicy>();
        foreach (var document in (documents ?? []).Take(MaximumPolicies))
        {
            if (document is not null && TryRead(document, out var policy, out _)) policies.Add(policy!);
        }

        return policies;
    }
}
