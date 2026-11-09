namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Runs the passes that bind the sessions of a project to its new folder (<see cref="OwnedSessionCommandService.FollowMovedProjectFoldersAsync"/>):
/// when the host starts, when a project file is written, and when the session list finds a session whose project moved.
/// One pass runs at a time, and the requests made meanwhile give one more. A session the list finds again after a pass
/// left it (it was at work) does not start another pass by itself: the next change of a project, or the next start, does.
/// </summary>
internal sealed class MovedProjectFolders
{
    private readonly Func<Task<OwnedSessionMoveResult>> _follow;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private Task? _running;
    private bool _again;
    private bool _busy;

    internal MovedProjectFolders(Func<Task<OwnedSessionMoveResult>> follow, Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(follow);
        _follow = follow;
        _delay = delay ?? (static wait => Task.Delay(wait));
    }

    /// <summary>The pass that runs, or the last one; completed when none ran.</summary>
    internal Task Current { get { lock (_gate) return _running ?? Task.CompletedTask; } }

    /// <summary>Runs a pass, or one more after the one that runs.</summary>
    internal void Follow()
    {
        lock (_gate)
        {
            if (_busy) { _again = true; return; }
            _busy = true;
            _running = RunAsync();
        }
    }

    /// <summary>A project file was written: the sessions a pass left are looked at again.</summary>
    internal void ProjectChanged()
    {
        lock (_gate) _seen.Clear();
        Follow();
    }

    /// <summary>The session list found these sessions (<c>id|folder</c>) whose project moved: a pass runs for the ones not seen yet.</summary>
    internal void Found(IReadOnlyList<string> sessions)
    {
        lock (_gate)
        {
            var fresh = false;
            foreach (var session in sessions) fresh |= _seen.Add(session);
            if (!fresh) return;
        }
        Follow();
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        for (var attempt = 0; ; attempt++)
        {
            lock (_gate) _again = false;
            OwnedSessionMoveResult result;
            try { result = await _follow().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { result = OwnedSessionMoveResult.Refused("move_unconfirmed"); }
            // Another change of the catalog held it: it is tried again a little later, a few times.
            var busy = result.Status == "busy" && attempt < 5;
            if (busy) await _delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            lock (_gate)
            {
                if (busy || _again) continue;
                _busy = false;
                return;
            }
        }
    }
}
