using CodeAlta.Tui.Presentation.Timeline;

namespace CodeAlta.Tui.App.State;

internal sealed class SessionTimelineState
{
    public SessionTimelineState(SessionTimelinePresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        Presenter = presenter;
    }

    public SessionTimelinePresenter Presenter { get; }
}
