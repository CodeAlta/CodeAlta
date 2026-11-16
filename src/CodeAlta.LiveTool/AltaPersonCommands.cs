using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.LiveTool;

/// <summary>
/// Runs an <c>alta</c> command for the person of the host, not for an agent: what a host answers a device of that person
/// with, when the device reads a session of this machine (<c>sessions.read</c> of the node API). The caller builds the
/// command; nothing here checks what it may read.
/// </summary>
public static class AltaPersonCommands
{
    /// <summary>The most records of one answer.</summary>
    public const int MaxRecords = 5000;

    /// <summary>Runs one command.</summary>
    /// <param name="dispatcher">The commands of the host.</param>
    /// <param name="args">The words of the command, without <c>alta</c>.</param>
    /// <param name="maxBytes">The most bytes of its output.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>Its exit code, its records (one JSON object a line), whether they were cut, and what failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/> or <paramref name="args"/> is null.</exception>
    public static async Task<(int ExitCode, string Jsonl, bool Truncated, string? Error)> RunAsync(AltaCommandDispatcher dispatcher, IReadOnlyList<string> args, int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(args);
        var result = await dispatcher.InvokeAsync(args, string.Empty, new AltaCallerIdentity { Kind = "host", OnBehalfOfUser = true }, null, MaxRecords, maxBytes, cancellationToken)
            .ConfigureAwait(false);
        return (result.ExitCode, result.Stdout, result.Truncated, result.Error);
    }
}
