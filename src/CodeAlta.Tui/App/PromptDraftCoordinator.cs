using CodeAlta.Tui.Models;
using static CodeAlta.Catalog.PromptDraftStore;

namespace CodeAlta.Tui.App;

internal sealed class PromptDraftCoordinator
{
    private readonly Dictionary<string, string> _draftPromptTextByScope = new(StringComparer.OrdinalIgnoreCase);

    public PromptDraftChange RememberPrompt(SessionState? session, string? text, string? draftScopeKey = null)
    {
        var normalized = text ?? string.Empty;
        var previous = session?.PromptDraftText ?? GetDraftPrompt(NormalizeDraftScopeKey(draftScopeKey));
        var textChanged = !string.Equals(previous, normalized, StringComparison.Ordinal);
        var editedStateChanged = string.IsNullOrWhiteSpace(previous) != string.IsNullOrWhiteSpace(normalized);

        if (session is null)
        {
            _draftPromptTextByScope[NormalizeDraftScopeKey(draftScopeKey)] = normalized;
            return new PromptDraftChange(textChanged, editedStateChanged);
        }

        session.PromptDraftText = normalized;
        return new PromptDraftChange(textChanged, editedStateChanged);
    }

    public string GetPrompt(SessionState? session, string? draftScopeKey = null)
        => session?.PromptDraftText ?? GetDraftPrompt(NormalizeDraftScopeKey(draftScopeKey));

    public bool TryGetDraftPrompt(string? draftScopeKey, out string prompt)
        => _draftPromptTextByScope.TryGetValue(NormalizeDraftScopeKey(draftScopeKey), out prompt!);

    public bool HasDraftPrompt(string? draftScopeKey)
        => !string.IsNullOrWhiteSpace(GetPrompt(session: null, draftScopeKey));

    private string GetDraftPrompt(string draftScopeKey)
        => _draftPromptTextByScope.GetValueOrDefault(draftScopeKey) ?? string.Empty;
}
