using CodeAlta.Presentation.Styling;
using CodeAlta.Presentation.Shell;
using CodeAlta.ViewModels;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Views;

internal sealed class ThreadStatusLineView
{
    private Markup? _statusIconVisual;

    public ThreadStatusLineView(
        CodeAltaShellViewModel shellViewModel,
        State<float> thinkingAnimationPhase01,
        Func<Visual?>? buildPluginStatusVisual = null)
    {
        ArgumentNullException.ThrowIfNull(shellViewModel);
        ArgumentNullException.ThrowIfNull(thinkingAnimationPhase01);

        var statusSpinner = new Spinner().Style(SpinnerStyles.Dots);
        statusSpinner.IsActive(() => shellViewModel.StatusBusy);
        statusSpinner.IsVisible(() => shellViewModel.StatusBusy);

        var statusPrefix = new Center(
            new ComputedVisual(
                () => shellViewModel.StatusBusy
                    ? statusSpinner
                    : _statusIconVisual ??= new Markup(() => shellViewModel.StatusIconMarkup)
                    {
                        Wrap = false,
                    }))
        {
            MinWidth = 2,
            MaxWidth = 2,
        };

        var statusText = new TextBlock
            {
                Wrap = true,
                IsSelectable = false,
            }.Text(() => shellViewModel.StatusText)
            .Style(() => StatusVisualFormatter.BuildStatusTextStyle(shellViewModel.StatusText, shellViewModel.StatusBusy, shellViewModel.StatusTone, thinkingAnimationPhase01.Value));
        var statusLineLeft = new HStack(
            new Visual[]
            {
                statusPrefix,
                statusText,
            })
            {
                Spacing = 1,
                HorizontalAlignment = Align.Stretch,
            };
        var providerSessionLoadStatus = new TextBlock
            {
                Wrap = false,
                IsSelectable = false,
            }.Text(() => shellViewModel.ProviderSessionLoadStatusText)
            .Style(TextBlockStyle.Default with { Foreground = UiPalette.WelcomeGuidanceColor });
        Visual statusLineRight = buildPluginStatusVisual is null
            ? providerSessionLoadStatus
            : new HStack(
            [
                new ComputedVisual(() => buildPluginStatusVisual() ?? new Placeholder { IsVisible = false }),
                providerSessionLoadStatus,
            ])
            {
                Spacing = 2,
            };
        Root = new StatusBar()
            .LeftText(statusLineLeft)
            .RightText(statusLineRight);
    }

    public StatusBar Root { get; }
}
