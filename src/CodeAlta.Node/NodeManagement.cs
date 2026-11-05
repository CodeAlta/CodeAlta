using System.Text;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Update;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// The log and the self-update of a running node, for its owner's local control and the devices that manage it
/// (<c>control.log</c>, <c>control.update</c>): the same file <c>altanode logs</c> reads, and the same update
/// <c>altanode update</c> asks for, carried out by <see cref="NodeUpdates"/> when the node is idle.
/// </summary>
/// <param name="profile">The profile of the node.</param>
/// <param name="running">The version the node runs.</param>
/// <param name="updates">The self-update of the node once it started; null while it does not run (an unparsable version).</param>
internal sealed class NodeManagement(CodeAltaInstanceProfile profile, string running, Func<NodeUpdates?> updates) : IFleetNodeManagement
{
    /// <summary>The most bytes of log one answer carries: the end is kept.</summary>
    internal const int MaxLogBytes = 256 * 1024;

    /// <inheritdoc />
    public async Task<FleetNodeLog> ReadLogAsync(int lines, CancellationToken cancellationToken)
    {
        var path = NodeLogging.GetLogFilePath(profile.StateRoot);
        var text = await Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return NodeLogCommand.Tail(stream, lines);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return "";
            }
        }, cancellationToken).ConfigureAwait(false);
        var truncated = false;
        if (Encoding.UTF8.GetByteCount(text) > MaxLogBytes)
        {
            // The end of a long log matters most: whole lines from the last bytes.
            var start = text.Length - MaxLogBytes / 2;
            var newline = text.IndexOf('\n', Math.Max(0, start));
            text = newline >= 0 ? text[(newline + 1)..] : text[^(MaxLogBytes / 2)..];
            truncated = true;
        }

        return new FleetNodeLog(text, path, truncated);
    }

    /// <inheritdoc />
    public async Task<FleetNodeUpdateResult> UpdateAsync(FleetNodeUpdateParams parameters, string requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(requestedBy);
        if (updates() is not { } self) return new FleetNodeUpdateResult(FleetNodeUpdateResult.Unavailable, running, Message: "The self-update of this node is not running.");
        var request = new UpdateRequest { Version = parameters.Version, Now = parameters.Now };
        try
        {
            var check = await self.CheckAsync(request, cancellationToken).ConfigureAwait(false);
            var latest = check.Latest?.ToString();
            if (check.Target is not { } target) return new FleetNodeUpdateResult(FleetNodeUpdateResult.UpToDate, running, latest, Message: check.Note);
            if (parameters.Check) return new FleetNodeUpdateResult(FleetNodeUpdateResult.Available, running, latest, target.ToString(), check.Note);
            var staged = await self.ApplyAsync(request, requestedBy, cancellationToken).ConfigureAwait(false);
            return staged is null
                ? new FleetNodeUpdateResult(FleetNodeUpdateResult.UpToDate, running, latest)
                : new FleetNodeUpdateResult(FleetNodeUpdateResult.Staged, running, latest, staged.Version.ToString(),
                    parameters.Now ? "The node switches now: its running turns stop and can be resumed." : "The node switches once no session works.");
        }
        catch (UpdateException exception)
        {
            return new FleetNodeUpdateResult(FleetNodeUpdateResult.Failed, running, Message: exception.Message);
        }
    }
}
