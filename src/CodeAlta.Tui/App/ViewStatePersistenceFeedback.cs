using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tui.App;

// UI-owned feedback only. Persistence retains the pending snapshot and acknowledged revision.
internal sealed class ViewStatePersistenceFeedback
{
    private readonly Action<string, bool, StatusTone> _setStatus;
    private bool _reportedFailure;

    public ViewStatePersistenceFeedback(Action<string, bool, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(setStatus);
        _setStatus = setStatus;
    }

    public bool Report(SessionViewStateCoordinator.PersistenceResult result)
    {
        if (result.IsAcknowledged)
        {
            if (_reportedFailure)
            {
                _setStatus(SR.T("Pending workspace changes saved."), false, StatusTone.Info);
                _reportedFailure = false;
            }

            return true;
        }

        _reportedFailure = true;
        if (result.Save is { IsConflict: true })
        {
            _setStatus(SR.T("Workspace state changed externally. Changes remain pending and were not saved."), false, StatusTone.Warning);
        }
        else
        {
            _setStatus(SR.T("Workspace changes remain pending. Save failed: {0}", result.Error?.Message ?? SR.T("Unknown error")), false, StatusTone.Error);
        }

        return false;
    }
}
