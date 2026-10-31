using CodeAlta.Agent;

namespace CodeAlta.Desktop.Terminals;

/// <summary>
/// A pseudo-terminal of the window for a program that refuses to run without one (<c>claude --cloud</c>): it runs in a
/// terminal of the application with no tab, wide enough that what it prints is not wrapped. When it waits for the user
/// (a question it asks, or a screen that stays still), its tab is opened, and the user answers there. Once it ended
/// well its terminal is closed; after a failure it is kept, to be looked at. What it shows is never logged.
/// </summary>
internal sealed class DesktopInteractiveTerminal : IAgentInteractiveTerminal
{
    /// <summary>The width of the screen of the program: an address it prints fits on one row.</summary>
    internal const int Columns = 220;

    private const int Rows = 40;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(5);

    private readonly DesktopTerminals _terminals;
    private readonly string? _projectId;
    private readonly TimeProvider _time;

    /// <summary>Creates the terminal of a project.</summary>
    /// <param name="terminals">The terminals of the application.</param>
    /// <param name="projectId">The project the terminal is listed under, or null.</param>
    /// <param name="time">The clock, or null for the system one.</param>
    /// <param name="quietBeforeShown">How long a screen that does not change, of a program that still runs, waits before it is shown; null for 10 s.</param>
    internal DesktopInteractiveTerminal(DesktopTerminals terminals, string? projectId, TimeProvider? time = null, TimeSpan? quietBeforeShown = null)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        (_terminals, _projectId, _time) = (terminals, projectId, time ?? TimeProvider.System);
        QuietBeforeShown = quietBeforeShown ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>How long a screen that does not change waits before it is shown.</summary>
    internal TimeSpan QuietBeforeShown { get; }

    /// <inheritdoc />
    public async Task<AgentInteractiveOutcome> RunAsync(AgentInteractiveProgram program, Func<IReadOnlyList<string>, bool> asks, Action? onShown,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(asks);
        var (status, terminal) = _terminals.Create(
            new TerminalRequest(_projectId, null, program.WorkingDirectory, null, program.Title, Agent: false, Columns, Rows, Integration: false),
            new TerminalProgram(program.FileName, program.Arguments, program.Environment));
        if (terminal is null) throw new InvalidOperationException($"No terminal could run Claude Code ({status}).");

        var shown = false;
        var canceled = false;
        IReadOnlyList<string> rows = [];
        string? last = null;
        var still = _time.GetTimestamp();
        try
        {
            while (!terminal.Ended.IsCompleted)
            {
                try
                {
                    await Task.WhenAny(terminal.Ended, Task.Delay(Tick, _time, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }

                rows = terminal.ReadScreen().Rows;
                var text = string.Join('\n', rows);
                if (!string.Equals(text, last, StringComparison.Ordinal)) (last, still) = (text, _time.GetTimestamp());
                if (!shown && !terminal.Ended.IsCompleted && (asks(rows) || _time.GetElapsedTime(still) >= QuietBeforeShown))
                {
                    shown = true;
                    _terminals.Reveal(terminal);
                    onShown?.Invoke();
                }
            }

            if (canceled)
            {
                terminal.Close();
                await Task.WhenAny(terminal.Ended, Task.Delay(EndPatience, _time, CancellationToken.None)).ConfigureAwait(false);
            }

            rows = terminal.ReadScreen().Rows;
            var exitCode = canceled ? null : terminal.Describe().ExitCode;
            // A terminal that did its work is gone; one that failed stays, for its screen to be looked at.
            var kept = !canceled && exitCode != 0;
            if (!kept) terminal.Close();
            return new AgentInteractiveOutcome(rows, exitCode, canceled, kept ? terminal.Id : null, shown);
        }
        catch
        {
            terminal.Close();
            throw;
        }
    }
}
