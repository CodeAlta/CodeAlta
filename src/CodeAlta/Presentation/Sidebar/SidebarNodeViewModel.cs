using System.Globalization;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Presentation.Sidebar;

internal sealed partial class SidebarNodeViewModel
{
    public SidebarNodeViewModel(string nodeId, SidebarNodeKind kind, SidebarSelectionTarget? selectionTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        NodeId = nodeId;
        Kind = kind;
        SelectionTarget = selectionTarget;
        Title = string.Empty;
        InlineEditText = string.Empty;
        RelativeActivityText = string.Empty;
        ExactActivityText = string.Empty;
    }

    public string NodeId { get; }

    public SidebarNodeKind Kind { get; }

    public SidebarSelectionTarget? SelectionTarget { get; }

    public DateTimeOffset? ActivityAtUtc { get; private set; }

    public DateTimeOffset? NextRelativeRefreshAtUtc { get; private set; }

    [Bindable]
    public partial string Title { get; set; }

    [Bindable]
    public partial string InlineEditText { get; set; }

    [Bindable]
    public partial bool IsInlineEditing { get; set; }

    [Bindable]
    public partial ValidationMessage? InlineEditValidationMessage { get; set; }

    [Bindable]
    public partial string RelativeActivityText { get; set; }

    [Bindable]
    public partial string ExactActivityText { get; set; }

    [Bindable]
    public partial bool ShowStateSpinner { get; set; }

    [Bindable]
    public partial string? StateIconMarkup { get; set; }

    [Bindable]
    public partial string? StateTooltip { get; set; }

    public void UpdateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!string.Equals(Title, title, StringComparison.Ordinal))
        {
            Title = title;
        }
    }

    public void BeginInlineEdit()
    {
        InlineEditText = Title;
        InlineEditValidationMessage = null;
        IsInlineEditing = true;
    }

    public void CancelInlineEdit()
    {
        IsInlineEditing = false;
        InlineEditText = Title;
        InlineEditValidationMessage = null;
    }

    public bool TryGetInlineEditValue(out string displayName)
    {
        var validationMessage = ValidateInlineEditText(InlineEditText);
        InlineEditValidationMessage = validationMessage;
        if (validationMessage is not null)
        {
            displayName = string.Empty;
            return false;
        }

        displayName = InlineEditText.Trim();
        return true;
    }

    public void UpdateActivity(DateTimeOffset? activityAtUtc, DateTimeOffset nowUtc)
    {
        ActivityAtUtc = activityAtUtc;
        var display = SidebarRelativeTimeFormatter.Format(activityAtUtc, nowUtc);
        if (!string.Equals(RelativeActivityText, display.RelativeText, StringComparison.Ordinal))
        {
            RelativeActivityText = display.RelativeText;
        }

        if (!string.Equals(ExactActivityText, display.ExactText, StringComparison.Ordinal))
        {
            ExactActivityText = display.ExactText;
        }

        NextRelativeRefreshAtUtc = display.NextRefreshAtUtc;
    }

    public void UpdateStateIndicator(string? iconMarkup, bool showSpinner, string? tooltip = null)
    {
        if (ShowStateSpinner != showSpinner)
        {
            ShowStateSpinner = showSpinner;
        }

        if (!string.Equals(StateIconMarkup, iconMarkup, StringComparison.Ordinal))
        {
            StateIconMarkup = iconMarkup;
        }

        if (!string.Equals(StateTooltip, tooltip, StringComparison.Ordinal))
        {
            StateTooltip = tooltip;
        }
    }

    partial void OnInlineEditTextChanged(string value)
    {
        if (!IsInlineEditing)
        {
            return;
        }

        InlineEditValidationMessage = ValidateInlineEditText(value);
    }

    partial void OnIsInlineEditingChanged(bool value)
    {
        if (value)
        {
            InlineEditValidationMessage = ValidateInlineEditText(InlineEditText);
            return;
        }

        InlineEditValidationMessage = null;
    }

    private static ValidationMessage? ValidateInlineEditText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ValidationMessage(ValidationSeverity.Error, "Display name is required.");
        }

        return null;
    }
}

internal static class SidebarRelativeTimeFormatter
{
    public static SidebarRelativeTimeDisplay Format(DateTimeOffset? activityAtUtc, DateTimeOffset nowUtc)
    {
        if (activityAtUtc is null || activityAtUtc == default)
        {
            return new SidebarRelativeTimeDisplay("never", "No recorded activity.", null);
        }

        var effectiveNow = nowUtc < activityAtUtc.Value ? activityAtUtc.Value : nowUtc;
        var elapsed = effectiveNow - activityAtUtc.Value;
        var exact = activityAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return new SidebarRelativeTimeDisplay("just now", exact, activityAtUtc.Value.AddMinutes(1));
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Floor(elapsed.TotalMinutes));
            return new SidebarRelativeTimeDisplay(
                FormatUnit(minutes, "min"),
                exact,
                activityAtUtc.Value.AddMinutes(minutes + 1));
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Floor(elapsed.TotalHours));
            return new SidebarRelativeTimeDisplay(
                FormatUnit(hours, "h"),
                exact,
                activityAtUtc.Value.AddHours(hours + 1));
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            var days = Math.Max(1, elapsed.Days);
            return new SidebarRelativeTimeDisplay(
                FormatLongUnit(days, "day"),
                exact,
                activityAtUtc.Value.AddDays(days + 1));
        }

        if (elapsed < TimeSpan.FromDays(365))
        {
            var months = Math.Max(1, elapsed.Days / 30);
            return new SidebarRelativeTimeDisplay(
                FormatLongUnit(months, "month"),
                exact,
                activityAtUtc.Value.AddDays((months + 1) * 30));
        }

        var years = Math.Max(1, elapsed.Days / 365);
        return new SidebarRelativeTimeDisplay(
            FormatLongUnit(years, "year"),
            exact,
            activityAtUtc.Value.AddDays((years + 1) * 365));
    }

    private static string FormatUnit(int value, string unit)
        => value == 1 ? $"1 {unit} ago" : $"{value} {unit} ago";

    private static string FormatLongUnit(int value, string unit)
        => value == 1 ? $"1 {unit} ago" : $"{value} {unit}s ago";
}

internal readonly record struct SidebarRelativeTimeDisplay(
    string RelativeText,
    string ExactText,
    DateTimeOffset? NextRefreshAtUtc);
