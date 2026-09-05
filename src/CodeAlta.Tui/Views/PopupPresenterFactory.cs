using CodeAlta.Tui.App;
using CodeAlta.Tui.Presentation.Sessions;
using CodeAlta.Tui.Presentation.Usage;
using CodeAlta.Tui.ViewModels;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal static class PopupPresenterFactory
{
    public static SessionUsagePresenter CreateSessionUsagePresenter(
        SessionUsageViewModel viewModel,
        Func<TerminalApp?> getApp,
        Func<Visual?> getPromptEditor,
        Func<Func<Visual>, Visual> createComputedVisual)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(getApp);
        ArgumentNullException.ThrowIfNull(getPromptEditor);
        ArgumentNullException.ThrowIfNull(createComputedVisual);

        return new SessionUsagePresenter(
            viewModel,
            markdown => getApp()?.Terminal.Clipboard.TrySetText(markdown),
            createComputedVisual,
            () => getApp()?.Focus(getPromptEditor()));
    }

    public static SessionInfoPresenter CreateSessionInfoPresenter(
        Func<TerminalApp?> getApp,
        Func<Visual?> getPromptEditor,
        SessionInfoService sessionInfoService,
        Action<Action> dispatchToUi,
        Func<Func<Visual>, Visual> createComputedVisual)
    {
        ArgumentNullException.ThrowIfNull(getApp);
        ArgumentNullException.ThrowIfNull(getPromptEditor);
        ArgumentNullException.ThrowIfNull(sessionInfoService);
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(createComputedVisual);

        return new SessionInfoPresenter(
            markdown => getApp()?.Terminal.Clipboard.TrySetText(markdown),
            cancellationToken => sessionInfoService.LoadSelectedSessionReportAsync(cancellationToken),
            dispatchToUi,
            createComputedVisual,
            () => getApp()?.Focus(getPromptEditor()));
    }
}
