using System.Globalization;

namespace CodeAlta.CodeMode;

/// <summary>Which recorded steps a run replays.</summary>
public enum CodeModeReplayMode
{
    /// <summary>
    /// The longest unchanged prefix: the n-th step that starts replays when the n-th recorded step has its key and
    /// completed with a result. The first step that does not match, and every step after it, runs.
    /// </summary>
    Resume,

    /// <summary>Every step whose key a completed recorded step has, wherever it is: only the changed code runs.</summary>
    All,

    /// <summary>The prefix, as <see cref="Resume"/>, up to a step: that step and what follows run ("rerun from this step").</summary>
    UntilStep,
}

/// <summary>A step a previous run recorded, as a replay reads it.</summary>
/// <param name="Index">The order in which it started in its run, from 0.</param>
/// <param name="Key">Its key, <c>&lt;id&gt;#&lt;occurrence&gt;</c>.</param>
/// <param name="Succeeded">Whether its function resolved.</param>
public sealed record CodeModeReplayStep(int Index, string Key, bool Succeeded)
{
    /// <summary>Gets the id <c>step()</c> was given, without the path of the scripts that start it; null to read it from <see cref="Key"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Gets the index of the step it started in, or null.</summary>
    public int? ParentIndex { get; init; }

    /// <summary>Gets whether its value was recorded (<see cref="CodeModeStepRecord.HasResult"/>).</summary>
    public bool HasResult { get; init; }

    /// <summary>Gets the JSON of its value; null with <see cref="HasResult"/> for <c>undefined</c>.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Gets whether a replay can give its value: it completed with a recorded result.</summary>
    public bool Replayable => Succeeded && HasResult;
}

/// <summary>
/// The steps a run replays from a previous run of the same script, and how it matches them. A replayed step does not
/// run its function: <c>step()</c> resolves with the value the previous run recorded. The steps of the scripts that
/// <c>workflow()</c> starts are matched as the others, by keys that start with the path of those scripts, each with the number of its call (<c>lib#0/read#0</c>).
/// </summary>
public sealed class CodeModeReplay
{
    /// <summary>The largest value of a step that is recorded, in UTF-8 bytes: a larger one is never replayed.</summary>
    public const int MaxResultBytes = 256 * 1024;

    /// <summary>Creates a replay.</summary>
    /// <param name="steps">The recorded steps, in any order; a step that started without ending is simply absent.</param>
    /// <param name="mode">How steps are matched.</param>
    /// <param name="untilKey">
    /// For <see cref="CodeModeReplayMode.UntilStep"/>, the step that runs live, such as <c>merge#1</c>, <c>merge</c> (its first
    /// occurrence), <c>lib/merge</c> or a bare id that names one step of a <c>workflow()</c> script only
    /// (<see cref="ResolveKey"/>).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="untilKey"/> is missing for <see cref="CodeModeReplayMode.UntilStep"/>, or two steps have the same index.</exception>
    public CodeModeReplay(IEnumerable<CodeModeReplayStep> steps, CodeModeReplayMode mode, string? untilKey = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (mode == CodeModeReplayMode.UntilStep && string.IsNullOrWhiteSpace(untilKey))
        {
            throw new ArgumentException("A replay until a step names the step.", nameof(untilKey));
        }

        var ordered = steps.OrderBy(static step => step.Index).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Index == ordered[i - 1].Index)
            {
                throw new ArgumentException($"Two recorded steps have the index {ordered[i].Index}.", nameof(steps));
            }
        }

        Steps = ordered;
        Mode = mode;
        UntilKey = mode == CodeModeReplayMode.UntilStep ? ResolveKey(untilKey!, ordered.Select(static step => step.Key), out _) ?? NormalizeKey(untilKey!) : null;
    }

    /// <summary>
    /// Finds the key a person or a model typed among recorded keys. The id of a step of a <c>workflow()</c> script starts
    /// with the path of the scripts, each with the number of its call (<c>lib#1/read#0</c>). What was typed is the full key,
    /// the full id (its first occurrence), the path without the numbers of the calls (<c>lib/read</c>), or the id alone
    /// (<c>read</c>, <c>read#1</c>): the last two when one full id only has them.
    /// </summary>
    /// <param name="typed">What was typed.</param>
    /// <param name="keys">The recorded keys.</param>
    /// <param name="candidates">The full ids it could name, when there are several.</param>
    /// <returns>The full key, or null when no recorded step has it or it names several.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typed"/> or <paramref name="keys"/> is null.</exception>
    public static string? ResolveKey(string typed, IEnumerable<string> keys, out IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(keys);
        candidates = [];
        var known = keys.ToHashSet(StringComparer.Ordinal);
        var normalized = NormalizeKey(typed);
        if (known.Contains(normalized))
        {
            return normalized;
        }

        var (id, occurrence) = ParseKey(normalized);
        var path = WithoutCallNumbers(id);
        var ids = known.Select(static key => ParseKey(key).Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // The same path first; else the paths that end with it, as a bare id does.
        var same = ids.Where(full => WithoutCallNumbers(full) == path).ToArray();
        var matches = same.Length > 0 ? same : ids.Where(full => WithoutCallNumbers(full).EndsWith("/" + path, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 1)
        {
            var key = KeyOf(matches[0], occurrence);
            return known.Contains(key) ? key : null;
        }

        candidates = matches;
        return null;
    }

    // `a#0/b#1/read` is `a/b/read`: the numbers of the workflow() calls go, the id of the step stays as it is.
    private static string WithoutCallNumbers(string id)
    {
        var segments = id.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            segments[i] = ParseKey(segments[i]).Id;
        }

        return string.Join('/', segments);
    }

    /// <summary>Gets the recorded steps, in the order they started.</summary>
    public IReadOnlyList<CodeModeReplayStep> Steps { get; }

    /// <summary>Gets how steps are matched.</summary>
    public CodeModeReplayMode Mode { get; }

    /// <summary>Gets the step that runs live for <see cref="CodeModeReplayMode.UntilStep"/>, as a full key; null otherwise.</summary>
    public string? UntilKey { get; }

    /// <summary>Gives the key of a step: its id and how many steps of that id started before it in the run.</summary>
    /// <param name="id">The id of the step.</param>
    /// <param name="occurrence">How many steps of that id started before it.</param>
    /// <returns>The key, such as <c>merge#1</c>.</returns>
    public static string KeyOf(string id, int occurrence) => string.Create(CultureInfo.InvariantCulture, $"{id}#{occurrence}");

    /// <summary>Reads a key: the id, and the occurrence after the last <c>#</c>; a bare id is its first occurrence.</summary>
    /// <param name="key">The key, such as <c>merge#1</c>, or an id.</param>
    /// <returns>The id and the occurrence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static (string Id, int Occurrence) ParseKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var hash = key.LastIndexOf('#');
        return hash > 0 && int.TryParse(key.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var occurrence)
            ? (key[..hash], occurrence)
            : (key, 0);
    }

    /// <summary>Gives the full key of a key or of a bare id (<c>merge</c> is <c>merge#0</c>).</summary>
    /// <param name="key">The key or the id.</param>
    /// <returns>The full key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static string NormalizeKey(string key)
    {
        var (id, occurrence) = ParseKey(key.Trim());
        return KeyOf(id, occurrence);
    }

    /// <summary>Describes a step for a person: <c>`merge`</c>, or <c>`merge` (2nd occurrence)</c> after the first.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public static string Describe(string key)
    {
        var (id, occurrence) = ParseKey(key);
        if (occurrence == 0)
        {
            return $"`{id}`";
        }

        var nth = occurrence + 1;
        var suffix = (nth % 100) is 11 or 12 or 13 ? "th" : (nth % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return string.Create(CultureInfo.InvariantCulture, $"`{id}` ({nth}{suffix} occurrence)");
    }

    internal Cursor Start() => new(this);

    // What a run decides for each step that starts. A run has one, used on the engine's thread only.
    internal sealed class Cursor
    {
        private readonly CodeModeReplay _replay;
        private readonly Dictionary<int, CodeModeReplayStep> _byIndex = [];
        private readonly Dictionary<string, CodeModeReplayStep> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<int, List<CodeModeReplayStep>> _children = [];
        private readonly HashSet<int> _consumed = [];
        private readonly int _end;
        private int _next;
        private bool _live;
        private bool _reported;

        public Cursor(CodeModeReplay replay)
        {
            _replay = replay;
            foreach (var step in replay.Steps)
            {
                _byIndex[step.Index] = step;
                _byKey.TryAdd(step.Key, step);
                if (step.ParentIndex is { } parent)
                {
                    if (!_children.TryGetValue(parent, out var children))
                    {
                        _children[parent] = children = [];
                    }

                    children.Add(step);
                }
            }

            _end = replay.Steps.Count == 0 ? 0 : replay.Steps[^1].Index + 1;
        }

        // The recorded step a step that starts replays, or null when it runs; report is what the output says, once.
        public CodeModeReplayStep? Decide(string key, out string? report)
        {
            report = null;
            if (_replay.Mode == CodeModeReplayMode.All)
            {
                if (_byKey.TryGetValue(key, out var match) && match.Replayable && _consumed.Add(match.Index))
                {
                    return match;
                }

                report = Once($"Rerun: step {Describe(key)} has no recorded result; it runs live.");
                return null;
            }

            if (_live)
            {
                return null;
            }

            // The steps a replayed step started are replayed with it: the next one to match comes after them.
            while (_next < _end && _consumed.Contains(_next))
            {
                _next++;
            }

            if (_replay.UntilKey == key)
            {
                _live = true;
                report = Once($"Rerun from step {Describe(key)}: it and what follows run live.");
                return null;
            }

            var recorded = _next < _end ? _byIndex.GetValueOrDefault(_next) : null;
            _next++;
            var prefix = _replay.Mode == CodeModeReplayMode.Resume ? "Resume" : "Rerun";
            if (recorded is not null && recorded.Key != key)
            {
                _live = true;
                report = Once($"{prefix}: diverged at step {Describe(key)}; it and what follows run live.");
                return null;
            }

            if (recorded is null || !recorded.Replayable)
            {
                _live = true;
                report = Once($"{prefix}: step {Describe(key)} has no recorded result; it and what follows run live.");
                return null;
            }

            _consumed.Add(recorded.Index);
            return recorded;
        }

        // The recorded steps a replayed step started, at any depth, in the order they started.
        public IReadOnlyList<CodeModeReplayStep> Descendants(CodeModeReplayStep step)
        {
            var found = new List<CodeModeReplayStep>();
            Collect(step.Index);
            found.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            return found;

            void Collect(int index)
            {
                if (!_children.TryGetValue(index, out var children))
                {
                    return;
                }

                foreach (var child in children)
                {
                    if (_consumed.Add(child.Index))
                    {
                        found.Add(child);
                        Collect(child.Index);
                    }
                }
            }
        }

        private string? Once(string message)
        {
            if (_reported)
            {
                return null;
            }

            _reported = true;
            return message;
        }
    }
}
