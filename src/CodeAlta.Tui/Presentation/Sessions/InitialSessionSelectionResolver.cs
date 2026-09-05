using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.Presentation.Sessions;

internal static class InitialSessionSelectionResolver
{
    public static InitialSessionSelection Resolve(
        SessionViewViewState viewState,
        IReadOnlyList<SessionViewDescriptor> sessions)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(sessions);

        var selectedSessionId = viewState.SelectedSessionId ?? viewState.OpenSessionIds.FirstOrDefault();
        var selectedSession = string.IsNullOrWhiteSpace(selectedSessionId)
            ? null
            : sessions.FirstOrDefault(session => string.Equals(session.SessionId, selectedSessionId, StringComparison.OrdinalIgnoreCase));

        return new InitialSessionSelection(
            selectedSession?.SessionId,
            selectedSession?.SessionId);
    }
}
