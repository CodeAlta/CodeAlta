using System.Globalization;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// Describes an issue (a GitHub or GitLab issue, an Azure DevOps work item) that can be inserted into a prompt.
/// </summary>
/// <param name="Number">The issue number.</param>
/// <param name="Title">The issue title.</param>
/// <param name="Url">The issue URL.</param>
/// <param name="UpdatedAt">The issue update timestamp.</param>
/// <param name="State">The issue state, as the provider names it.</param>
/// <param name="IsOpen">Whether the provider still counts the issue as open.</param>
/// <param name="Repository">The repository full name.</param>
public sealed record GitIssueReferenceItem(int Number, string Title, string Url, DateTimeOffset UpdatedAt, string State, bool IsOpen, string Repository)
{
    /// <summary>Gets the displayed issue id.</summary>
    public string Id => FormattableString.Invariant($"#{Number}");

    /// <summary>Gets relative update text.</summary>
    public string UpdatedText => FormatRelativeTime(UpdatedAt, DateTimeOffset.UtcNow);

    /// <summary>Gets the displayed issue state.</summary>
    public string StateText => string.IsNullOrWhiteSpace(State) ? "unknown" : State;

    /// <summary>Gets link text.</summary>
    public string LinkText => string.IsNullOrWhiteSpace(Url) ? string.Empty : "Open";

    /// <summary>Gets Markdown inserted into the prompt.</summary>
    public string Markdown => FormattableString.Invariant($"[#{Number}]({Url})");

    private static string FormatRelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var duration = now - value.ToUniversalTime();
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalMinutes < 1)
        {
            return "just now";
        }

        if (duration.TotalHours < 1)
        {
            return FormattableString.Invariant($"{Math.Max(1, (int)duration.TotalMinutes)}m ago");
        }

        if (duration.TotalDays < 1)
        {
            return FormattableString.Invariant($"{Math.Max(1, (int)duration.TotalHours)}h ago");
        }

        if (duration.TotalDays < 30)
        {
            return FormattableString.Invariant($"{Math.Max(1, (int)duration.TotalDays)}d ago");
        }

        return value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }
}
