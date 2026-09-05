using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Shell;
using XenoAtom.Ansi;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tui.Presentation.Tabs;

internal static class SessionTabVisualFactory
{
    private const int MaxTabTitleLength = 18;

    public static string CompactTitle(string title)
    {
        var normalized = title.Trim();
        return normalized.Length <= MaxTabTitleLength
            ? normalized
            : normalized[..Math.Max(1, MaxTabTitleLength - 1)].TrimEnd() + "…";
    }

    public static OpenTabIndicatorKind ResolveIndicatorKind(bool isBusy, StatusTone tone)
        => ResolveIndicatorKind(isBusy, hasPromptDraft: false, tone);

    public static OpenTabIndicatorKind ResolveIndicatorKind(bool isBusy, bool hasPromptDraft, StatusTone tone)
    {
        if (isBusy)
        {
            return OpenTabIndicatorKind.Running;
        }

        if (hasPromptDraft)
        {
            return OpenTabIndicatorKind.Edited;
        }

        return tone switch
        {
            StatusTone.Warning => OpenTabIndicatorKind.Warning,
            StatusTone.Error => OpenTabIndicatorKind.Error,
            StatusTone.Info => OpenTabIndicatorKind.Info,
            _ => OpenTabIndicatorKind.Ready,
        };
    }

    public static Visual CreateIndicator(bool isBusy, StatusTone tone)
        => CreateIndicator(isBusy, hasPromptDraft: false, tone);

    public static Visual CreateIndicator(bool isBusy, bool hasPromptDraft, StatusTone tone)
    {
        var kind = ResolveIndicatorKind(isBusy, hasPromptDraft, tone);
        if (kind == OpenTabIndicatorKind.Running)
        {
            var spinner = new Spinner().Style(SpinnerStyles.Dots);
            spinner.IsActive(() => true);
            spinner.IsVisible(() => true);
            return spinner;
        }

        if (kind == OpenTabIndicatorKind.Edited)
        {
            return new Markup(StatusVisualFormatter.BuildPromptEditedIconMarkup())
            {
                Wrap = false,
            };
        }

        var statusTone = kind switch
        {
            OpenTabIndicatorKind.Warning => StatusTone.Warning,
            OpenTabIndicatorKind.Error => StatusTone.Error,
            OpenTabIndicatorKind.Info => StatusTone.Info,
            _ => StatusTone.Ready,
        };
        return new Markup(StatusVisualFormatter.BuildStatusIconMarkup(statusTone))
        {
            Wrap = false,
        };
    }

    public static Visual CreateTitle(string title)
    {
        return new Markup(AnsiMarkup.Escape(title))
        {
            Wrap = false,
        };
    }
}
