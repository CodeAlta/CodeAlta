using System.Globalization;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugin.GitHub;

/// <summary>
/// Describes a GitHub issue that can be inserted into a prompt.
/// </summary>
/// <param name="Number">The issue number.</param>
/// <param name="Title">The issue title.</param>
/// <param name="Url">The issue URL.</param>
/// <param name="UpdatedAt">The issue update timestamp.</param>
/// <param name="State">The issue state.</param>
/// <param name="Repository">The repository full name.</param>
public sealed record GitHubIssueReferenceItem(int Number, string Title, string Url, DateTimeOffset UpdatedAt, string State, string Repository)
{
    /// <summary>Gets the displayed issue id.</summary>
    public string Id => FormattableString.Invariant($"#{Number}");

    /// <summary>Gets relative update text.</summary>
    public string UpdatedText => FormatRelativeTime(UpdatedAt, DateTimeOffset.UtcNow);

    /// <summary>Gets the displayed issue state.</summary>
    public string StateText => string.IsNullOrWhiteSpace(State) ? "unknown" : State;

    /// <summary>Gets a value indicating whether the issue is open.</summary>
    public bool IsOpen => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase);

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

    internal static class Accessor
    {
        public static readonly BindingAccessor<string> Id = new("Id", static item => ((GitHubIssueReferenceItem)item).Id, null);

        public static readonly BindingAccessor<string> Title = new("Title", static item => ((GitHubIssueReferenceItem)item).Title, null);

        public static readonly BindingAccessor<string> State = new("State", static item => ((GitHubIssueReferenceItem)item).StateText, null);

        public static readonly BindingAccessor<string> Updated = new("Updated", static item => ((GitHubIssueReferenceItem)item).UpdatedText, null);

        public static readonly BindingAccessor<string> Link = new("Link", static item => ((GitHubIssueReferenceItem)item).LinkText, null);
    }
}
