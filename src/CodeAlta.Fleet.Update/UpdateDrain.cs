namespace CodeAlta.Fleet.Update;

/// <summary>What a node does at a step of the drain before a switch.</summary>
/// <param name="Phase">Where the switch stands (<see cref="UpdatePhases.Draining"/>, <see cref="UpdatePhases.WaitingForIdle"/>, <see cref="UpdatePhases.Switching"/>).</param>
/// <param name="PauseAdmission">Whether the node refuses new work now.</param>
/// <param name="Switch">Whether the node stops now to start the new version.</param>
public readonly record struct UpdateDrainStep(string Phase, bool PauseAdmission, bool Switch);

/// <summary>
/// When a node switches to a new version: when no session works. It first refuses new work and waits for the sessions
/// at work, within a bound; past the bound it takes work again and switches at the next idle moment. A switch always
/// follows a step that refused new work (a turn admitted just before is waited for, not cut). <c>--now</c> switches at once: the
/// stop of the node gives running turns their usual bound, then stops them (they are resumable).
/// </summary>
public sealed class UpdateDrain
{
    /// <summary>How long a node refuses new work while it waits for its sessions, by default.</summary>
    public static readonly TimeSpan DefaultBound = TimeSpan.FromMinutes(10);

    private readonly bool _now;
    private readonly TimeSpan _bound;
    private string _phase = UpdatePhases.Draining;
    private DateTimeOffset? _drainStarted;

    /// <summary>Initializes a new instance of the <see cref="UpdateDrain"/> class.</summary>
    /// <param name="now">Whether the switch does not wait for running work.</param>
    /// <param name="bound">How long new work is refused while the node waits for its sessions.</param>
    public UpdateDrain(bool now, TimeSpan bound)
    {
        _now = now;
        _bound = bound;
    }

    /// <summary>Decides the next step.</summary>
    /// <param name="sessionsAtWork">How many sessions work now.</param>
    /// <param name="now">The time.</param>
    /// <returns>What the node does.</returns>
    public UpdateDrainStep Step(int sessionsAtWork, DateTimeOffset now)
    {
        if (_now) return Done();
        if (_phase == UpdatePhases.Draining)
        {
            if (_drainStarted is null)
            {
                // New work is refused for one step before any switch: what was admitted just before is counted next time.
                _drainStarted = now;
                return new UpdateDrainStep(UpdatePhases.Draining, PauseAdmission: true, Switch: false);
            }

            if (sessionsAtWork == 0) return Done();
            if (now - _drainStarted.Value < _bound) return new UpdateDrainStep(UpdatePhases.Draining, PauseAdmission: true, Switch: false);
            _phase = UpdatePhases.WaitingForIdle;
            _drainStarted = null;
            return new UpdateDrainStep(UpdatePhases.WaitingForIdle, PauseAdmission: false, Switch: false);
        }

        if (_phase == UpdatePhases.WaitingForIdle)
        {
            if (sessionsAtWork > 0) return new UpdateDrainStep(UpdatePhases.WaitingForIdle, PauseAdmission: false, Switch: false);
            // Idle: new work is refused, and the next step switches if nothing started meanwhile.
            _phase = UpdatePhases.Draining;
            _drainStarted = null;
            return Step(sessionsAtWork, now);
        }

        return Done();
    }

    private UpdateDrainStep Done()
    {
        _phase = UpdatePhases.Switching;
        return new UpdateDrainStep(UpdatePhases.Switching, PauseAdmission: true, Switch: true);
    }
}
