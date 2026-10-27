namespace CodeAlta.CodeMode;

/// <summary>
/// The steps of one run and of the scripts its <c>workflow()</c> calls start: one counter of indexes, one count of the
/// occurrences of each id, and one replay. Nested scripts run on engines of their own, at the same time when the script
/// awaits several <c>workflow()</c> calls together, so every decision is taken under one lock.
/// </summary>
internal sealed class CodeModeStepSpace
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private readonly CodeModeReplay.Cursor? _cursor;
    private int _count;

    public CodeModeStepSpace(CodeModeReplay? replay) => _cursor = replay?.Start();

    /// <summary>
    /// A step starts: its key counts the steps of its full id (<c>lib#0/read</c>) started before it. It runs, or the replay
    /// gives it, and the steps it started in the recorded run, with their indexes in this run.
    /// </summary>
    /// <param name="fullId">The id of the step after the path of the scripts that start it.</param>
    /// <param name="parentIndex">The index of the step it starts in, or null.</param>
    public Begun Begin(string fullId, int? parentIndex)
    {
        lock (_gate)
        {
            var occurrence = _occurrences.GetValueOrDefault(fullId);
            _occurrences[fullId] = occurrence + 1;
            var key = CodeModeReplay.KeyOf(fullId, occurrence);
            string? report = null;
            if (_cursor?.Decide(key, out report) is not { } recorded)
            {
                return new Begun(key, _count++, null, report);
            }

            // The steps the replayed step started are replayed with it: they count toward the occurrences of their ids, and
            // keep their parents, mapped to the indexes of this run.
            var indexes = new Dictionary<int, int>();
            var replayed = new List<ReplayedStep>();
            var index = _count++;
            indexes[recorded.Index] = index;
            replayed.Add(new ReplayedStep(recorded, index, parentIndex));
            foreach (var descendant in _cursor.Descendants(recorded))
            {
                var (descendantId, _) = CodeModeReplay.ParseKey(descendant.Key);
                _occurrences[descendantId] = _occurrences.GetValueOrDefault(descendantId) + 1;
                var descendantIndex = _count++;
                indexes[descendant.Index] = descendantIndex;
                replayed.Add(new ReplayedStep(descendant, descendantIndex,
                    descendant.ParentIndex is { } parent && indexes.TryGetValue(parent, out var mapped) ? mapped : null));
            }

            return new Begun(key, index, replayed, null);
        }
    }

    /// <summary>What <see cref="Begin"/> decided.</summary>
    /// <param name="Key">The key of the step.</param>
    /// <param name="Index">Its index in the run.</param>
    /// <param name="Replayed">The steps a replay gives, the step itself first; null when it runs.</param>
    /// <param name="Report">What the output says of the replay, once; or null.</param>
    public sealed record Begun(string Key, int Index, IReadOnlyList<ReplayedStep>? Replayed, string? Report);

    /// <summary>A recorded step a replay gives, with its index and the index of its parent in this run.</summary>
    public sealed record ReplayedStep(CodeModeReplayStep Recorded, int Index, int? ParentIndex);
}
