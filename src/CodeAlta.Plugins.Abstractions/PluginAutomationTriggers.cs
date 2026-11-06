using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>What a value of the settings of a trigger holds.</summary>
public enum PluginTriggerValueKind
{
    /// <summary>A text.</summary>
    Text,

    /// <summary>True or false.</summary>
    Flag,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>A list of texts.</summary>
    Texts,
}

/// <summary>One value of the settings of a trigger, as the configuration file writes it.</summary>
public sealed record PluginTriggerValue
{
    private PluginTriggerValue(PluginTriggerValueKind kind, string? text, bool flag, long number, IReadOnlyList<string>? texts)
        => (Kind, Text, Flag, Number, Texts) = (kind, text, flag, number, texts ?? []);

    /// <summary>Gets what the value holds.</summary>
    public PluginTriggerValueKind Kind { get; }

    /// <summary>Gets the text of a <see cref="PluginTriggerValueKind.Text"/> value; null otherwise.</summary>
    public string? Text { get; }

    /// <summary>Gets the flag of a <see cref="PluginTriggerValueKind.Flag"/> value.</summary>
    public bool Flag { get; }

    /// <summary>Gets the number of a <see cref="PluginTriggerValueKind.Number"/> value.</summary>
    public long Number { get; }

    /// <summary>Gets the texts of a <see cref="PluginTriggerValueKind.Texts"/> value; empty otherwise.</summary>
    public IReadOnlyList<string> Texts { get; }

    /// <summary>Creates a text value.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static PluginTriggerValue OfText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(PluginTriggerValueKind.Text, text, false, 0, null);
    }

    /// <summary>Creates a flag value.</summary>
    /// <param name="flag">The flag.</param>
    /// <returns>The value.</returns>
    public static PluginTriggerValue OfFlag(bool flag) => new(PluginTriggerValueKind.Flag, null, flag, 0, null);

    /// <summary>Creates a number value.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The value.</returns>
    public static PluginTriggerValue OfNumber(long number) => new(PluginTriggerValueKind.Number, null, false, number, null);

    /// <summary>Creates a value that is a list of texts.</summary>
    /// <param name="texts">The texts, in their order.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="texts"/> or one of its texts is null.</exception>
    public static PluginTriggerValue OfTexts(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = texts.ToArray();
        if (Array.Exists(list, static text => text is null)) throw new ArgumentNullException(nameof(texts), "A list of texts holds no null.");
        return new(PluginTriggerValueKind.Texts, null, false, 0, list);
    }

    /// <summary>Gets the value as one line: what a key of the trigger is made of.</summary>
    /// <returns>The text of a text, <c>true</c> or <c>false</c>, the number, or the texts joined with commas.</returns>
    public override string ToString() => Kind switch
    {
        PluginTriggerValueKind.Text => Text!,
        PluginTriggerValueKind.Flag => Flag ? "true" : "false",
        PluginTriggerValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        _ => string.Join(',', Texts),
    };

    /// <summary>Gets whether two values hold the same.</summary>
    /// <param name="other">The other value.</param>
    /// <returns>True when they hold the same kind and the same content.</returns>
    public bool Equals(PluginTriggerValue? other)
        => other is not null && Kind == other.Kind && Text == other.Text && Flag == other.Flag && Number == other.Number && Texts.SequenceEqual(other.Texts, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(ToString()));
}

/// <summary>
/// The settings of a trigger: the keys of its table in the configuration file besides <c>type</c> and <c>on</c>, such as
/// <c>space = "work"</c>. The keys are kept in their ordinal order, so that two triggers that say the same read the same.
/// </summary>
public sealed class PluginTriggerSettings : IEquatable<PluginTriggerSettings>
{
    private readonly KeyValuePair<string, PluginTriggerValue>[] _values;

    /// <summary>Gets the settings that are empty.</summary>
    public static PluginTriggerSettings Empty { get; } = new([]);

    /// <summary>Creates settings.</summary>
    /// <param name="values">The values by key; a key given twice keeps its last value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/>, a key or a value is null.</exception>
    public PluginTriggerSettings(IEnumerable<KeyValuePair<string, PluginTriggerValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var byKey = new SortedDictionary<string, PluginTriggerValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(values));
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            byKey[key] = value;
        }

        _values = [.. byKey];
    }

    /// <summary>Gets how many keys the settings have.</summary>
    public int Count => _values.Length;

    /// <summary>Gets the keys and their values, in the ordinal order of the keys.</summary>
    public IReadOnlyList<KeyValuePair<string, PluginTriggerValue>> Values => _values;

    /// <summary>Gets the value of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null when the settings do not have the key.</returns>
    public PluginTriggerValue? Get(string key)
    {
        foreach (var (name, value) in _values)
        {
            if (string.Equals(name, key, StringComparison.Ordinal)) return value;
        }

        return null;
    }

    /// <summary>Gets the text of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The text, or null when the key is missing or is not a text.</returns>
    public string? Text(string key) => Get(key) is { Kind: PluginTriggerValueKind.Text } value ? value.Text : null;

    /// <summary>Gets the flag of a key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="fallback">What a missing key, or a key that is not a flag, gives.</param>
    /// <returns>The flag.</returns>
    public bool Flag(string key, bool fallback = false) => Get(key) is { Kind: PluginTriggerValueKind.Flag } value ? value.Flag : fallback;

    /// <summary>Gets the number of a key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The number, or null when the key is missing or is not a number.</returns>
    public long? Number(string key) => Get(key) is { Kind: PluginTriggerValueKind.Number } value ? value.Number : null;

    /// <summary>Gets the texts of a key: the list it holds, or the one text it holds.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The texts; empty when the key is missing or holds neither.</returns>
    public IReadOnlyList<string> Texts(string key) => Get(key) switch
    {
        { Kind: PluginTriggerValueKind.Texts } value => value.Texts,
        { Kind: PluginTriggerValueKind.Text } value => [value.Text!],
        _ => [],
    };

    /// <summary>Writes the settings as a JSON object.</summary>
    /// <returns>The JSON object, with the keys in their order.</returns>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in _values)
            {
                switch (value.Kind)
                {
                    case PluginTriggerValueKind.Text: writer.WriteString(key, value.Text); break;
                    case PluginTriggerValueKind.Flag: writer.WriteBoolean(key, value.Flag); break;
                    case PluginTriggerValueKind.Number: writer.WriteNumber(key, value.Number); break;
                    default:
                        writer.WriteStartArray(key);
                        foreach (var text in value.Texts) writer.WriteStringValue(text);
                        writer.WriteEndArray();
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <inheritdoc />
    public bool Equals(PluginTriggerSettings? other)
        => other is not null && _values.Length == other._values.Length
            && _values.Zip(other._values).All(static pair => pair.First.Key == pair.Second.Key && pair.First.Value.Equals(pair.Second.Value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginTriggerSettings);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _values) hash.Add(HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), value.GetHashCode()));
        return hash.ToHashCode();
    }
}

/// <summary>How a trigger source gives its events to the host.</summary>
[Flags]
public enum PluginTriggerDelivery
{
    /// <summary>The host asks for what happened since its cursor (<see cref="IAutomationTriggerSource.ReadAsync"/>).</summary>
    Polled = 1,

    /// <summary>The source keeps a listener running and hands each event as it comes (<see cref="IAutomationTriggerSource.ListenAsync"/>).</summary>
    Pushed = 2,
}

/// <summary>What a field of a trigger holds, which decides how the editor of automations draws it.</summary>
public enum PluginTriggerFieldKind
{
    /// <summary>One line of text.</summary>
    Text,

    /// <summary>A <c>${secret:NAME}</c> reference to a secret of the machine, never the secret itself. The host gives the source its value.</summary>
    Secret,

    /// <summary>One of the <see cref="PluginTriggerField.Choices"/>.</summary>
    Choice,

    /// <summary>Any number of the <see cref="PluginTriggerField.Choices"/>, a list of texts.</summary>
    Choices,

    /// <summary>True or false.</summary>
    Flag,

    /// <summary>A whole number.</summary>
    Number,

    /// <summary>The id of a space of CodeAlta.</summary>
    Space,

    /// <summary>The id of a project of the catalog.</summary>
    Project,

    /// <summary>The ids of automations, a list of texts.</summary>
    Automations,
}

/// <summary>Whether the automations a trigger starts run in a project.</summary>
public enum PluginTriggerProject
{
    /// <summary>In a project or as a chat: the trigger reads no project.</summary>
    Either,

    /// <summary>In a project only: the source reads it (<see cref="PluginTriggerContext.ProjectPath"/>).</summary>
    Required,
}

/// <summary>A value a field of a trigger offers.</summary>
/// <param name="Id">The value as the configuration file writes it.</param>
/// <param name="Label">The value as the user reads it.</param>
public sealed record PluginTriggerChoice(string Id, string Label);

/// <summary>An event a trigger can start on, such as <c>added</c>.</summary>
/// <param name="Id">The name of the event in the configuration file (<c>on = ["added"]</c>): a lowercase letter, then lowercase letters, digits and <c>_</c>.</param>
/// <param name="Label">The event as the user reads it, such as <c>Added</c>.</param>
public sealed record PluginTriggerEventType(string Id, string Label)
{
    /// <summary>Gets when the event happens, in a sentence; null for none.</summary>
    public string? Description { get; init; }
}

/// <summary>A setting of a trigger that the editor of automations offers.</summary>
/// <param name="Id">The key of the setting in the configuration file: a lowercase letter, then lowercase letters, digits and <c>_</c>.</param>
/// <param name="Label">The name of the setting as the user reads it.</param>
/// <param name="Kind">What it holds.</param>
public sealed record PluginTriggerField(string Id, string Label, PluginTriggerFieldKind Kind)
{
    /// <summary>Gets what the setting does, in a sentence; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets whether the trigger needs a value of it.</summary>
    public bool Required { get; init; }

    /// <summary>Gets the values of a <see cref="PluginTriggerFieldKind.Choice"/> or <see cref="PluginTriggerFieldKind.Choices"/> field.</summary>
    public IReadOnlyList<PluginTriggerChoice> Choices { get; init; } = [];

    /// <summary>Gets an example of a value, shown in the empty field; null for none.</summary>
    public string? Placeholder { get; init; }
}

/// <summary>
/// Lets a source of events ask the host to look at it now, rather than at the next look: a polled source that learned
/// something happened (a file changed, a message came). Create one, give it to the contribution, and call <see cref="Raise"/>.
/// </summary>
public sealed class PluginTriggerSignal
{
    /// <summary>Raised when the source asks for a look.</summary>
    public event Action? Raised;

    /// <summary>Asks the host to look at the source within a few seconds.</summary>
    public void Raise() => Raised?.Invoke();
}

/// <summary>
/// A trigger that a plugin gives to the automations of CodeAlta: its events, its settings and the source of its events.
/// </summary>
/// <remarks>
/// <para>
/// Return it from <see cref="PluginBase.GetAutomationTriggers"/>. A configuration file names it by its <see cref="Type"/>, as
/// <c>{ type = "acme.deploys/deploy", on = ["succeeded"], environment = "prod" }</c>: <c>on</c> chooses events, and the
/// other keys are the settings, which the host checks against <see cref="Fields"/> and the source checks with
/// <see cref="IAutomationTriggerSource.ValidateAsync"/>.
/// </para>
/// <para>
/// The host runs an automation once for each event, by its id, and writes what the event says into the prompt as someone else's
/// text. While the plugin is not active, the automations that use the trigger say so and run only by hand.
/// </para>
/// </remarks>
public sealed record PluginAutomationTriggerContribution
{
    /// <summary>The longest type of a trigger.</summary>
    public const int MaximumTypeLength = 128;

    /// <summary>The most events of a trigger.</summary>
    public const int MaximumEvents = 16;

    /// <summary>The most fields of a trigger.</summary>
    public const int MaximumFields = 32;

    /// <summary>
    /// Gets the type of the trigger, as the configuration file writes it: <c>&lt;prefix&gt;/&lt;name&gt;</c>, with a prefix
    /// of the plugin's own (lowercase letters, digits, <c>.</c>, <c>_</c> and <c>-</c>) and a name (a lowercase letter, then
    /// lowercase letters, digits and <c>_</c>), such as <c>acme.deploys/deploy</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>Gets the name of the trigger as the user reads it, such as <c>Deployment</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets when the trigger starts an automation, in a sentence; null for none.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the events the trigger can start on, the default first. A trigger has at least one.</summary>
    public required IReadOnlyList<PluginTriggerEventType> Events { get; init; }

    /// <summary>Gets the settings of the trigger.</summary>
    public IReadOnlyList<PluginTriggerField> Fields { get; init; } = [];

    /// <summary>Gets whether its automations run in a project.</summary>
    public PluginTriggerProject Project { get; init; } = PluginTriggerProject.Either;

    /// <summary>Gets the source of its events.</summary>
    public required IAutomationTriggerSource Source { get; init; }

    /// <summary>Gets the signal the source raises to be looked at now; null for a source that waits for the next look.</summary>
    public PluginTriggerSignal? Signal { get; init; }

    /// <summary>Gets the order among the triggers the editor offers; the lower comes first.</summary>
    public int Order { get; init; }

    /// <summary>Gets a message that says why the trigger is not valid, or <see langword="null"/> when it is.</summary>
    /// <returns>The reason, in a sentence.</returns>
    public string? Validate()
    {
        if (!IsType(Type)) return "has a type that is not <prefix>/<name>, such as acme.deploys/deploy";
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 80) return "has no display name, or one longer than 80 characters";
        if (Source is null) return "has no source";
        if (Events is not { Count: > 0 and <= MaximumEvents }) return $"has no event, or more than {MaximumEvents}";
        if (Events.Any(static type => type is null || !IsName(type.Id) || string.IsNullOrWhiteSpace(type.Label))) return "has an event without a name of lowercase letters, digits and _, or without a label";
        if (Events.Select(static type => type.Id).Distinct(StringComparer.Ordinal).Count() != Events.Count) return "names an event twice";
        if (Fields is null || Fields.Count > MaximumFields) return $"has more than {MaximumFields} fields";
        if (Fields.Any(static field => field is null || !IsName(field.Id) || field.Id is "type" or "on" || string.IsNullOrWhiteSpace(field.Label)))
            return "has a field without a key of lowercase letters, digits and _ (type and on are taken), or without a label";
        if (Fields.Select(static field => field.Id).Distinct(StringComparer.Ordinal).Count() != Fields.Count) return "names a field twice";
        if (Fields.Any(static field => field.Kind is PluginTriggerFieldKind.Choice or PluginTriggerFieldKind.Choices && field.Choices is not { Count: > 0 }))
            return "has a field of choices that offers none";
        return null;
    }

    /// <summary>Gets whether a text is the type of a trigger of a plugin: <c>&lt;prefix&gt;/&lt;name&gt;</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for a type.</returns>
    public static bool IsType(string? text)
    {
        if (text is not { Length: > 2 and <= MaximumTypeLength }) return false;
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash != text.LastIndexOf('/')) return false;
        var prefix = text.AsSpan(0, slash);
        if (!char.IsAsciiLetterLower(prefix[0]) && !char.IsAsciiDigit(prefix[0])) return false;
        return prefix.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789._-") < 0 && IsName(text.AsSpan(slash + 1));
    }

    /// <summary>Gets whether a text is the name of an event or of a field: a lowercase letter, then lowercase letters, digits and <c>_</c>, at most 64.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for a name.</returns>
    public static bool IsName(ReadOnlySpan<char> text)
        => text.Length is > 0 and <= 64 && char.IsAsciiLetterLower(text[0]) && text.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") < 0;
}

/// <summary>What the host tells a source about one trigger of one automation.</summary>
public sealed record PluginTriggerContext
{
    /// <summary>Gets the id of the automation.</summary>
    public required string AutomationId { get; init; }

    /// <summary>Gets a value that identifies the trigger among those of its automation, stable while it is not edited.</summary>
    public required string TriggerKey { get; init; }

    /// <summary>Gets the type of the trigger.</summary>
    public required string Type { get; init; }

    /// <summary>Gets the events the trigger starts on; the default event when the automation chose none.</summary>
    public required IReadOnlyList<string> Events { get; init; }

    /// <summary>Gets the settings of the trigger, with the value of each secret it names in place of its reference.</summary>
    public required PluginTriggerSettings Settings { get; init; }

    /// <summary>Gets the id of the project the automation runs in; null for a chat.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the folder of that project; null for a chat.</summary>
    public string? ProjectPath { get; init; }
}

/// <summary>Something that happened, which starts the automations whose trigger chose its event.</summary>
/// <param name="Id">What names the event in its source, so that it runs once, also when it is seen again: at most 200 characters.</param>
/// <param name="Event">The name of its event, one of <see cref="PluginAutomationTriggerContribution.Events"/>.</param>
/// <param name="Title">One line that says what happened, shown on the run and in the prompt.</param>
public sealed record PluginTriggerEvent(string Id, string Event, string Title)
{
    /// <summary>Gets when it happened; null when the source does not know.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>
    /// Gets what the prompt says of it beside its title, by name, in their order: each is one line, and is marked in the prompt as
    /// someone else's text, information to work with and not instructions.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; } = [];

    /// <summary>Gets the https address of what happened on the web; null for none.</summary>
    public string? Url { get; init; }

    /// <summary>Gets a JSON object a script of the automation receives as its <c>event</c> argument; null to give it the fields.</summary>
    public string? DataJson { get; init; }
}

/// <summary>What a polled source answers: what happened since its cursor.</summary>
/// <param name="Events">The events, the oldest first.</param>
/// <param name="Cursor">Where the next reading starts; the host keeps it with the trigger, across restarts.</param>
public sealed record PluginTriggerPage(IReadOnlyList<PluginTriggerEvent> Events, string? Cursor)
{
    /// <summary>Gets why the source could not say what happened, in words for the user; null when it could.</summary>
    public string? Problem { get; init; }
}

/// <summary>Where a pushed source hands its events.</summary>
public interface IPluginTriggerSink
{
    /// <summary>Hands an event to the host, which starts the automation for it unless it ran for this id already.</summary>
    /// <param name="triggerEvent">The event.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True when a run started, or the event ran already; false when the automation could not start now (it is running, paused or off), so that the source can hand it again later.</returns>
    ValueTask<bool> PublishAsync(PluginTriggerEvent triggerEvent, CancellationToken cancellationToken);

    /// <summary>Says why the listener receives nothing, shown under the trigger; null when it works again.</summary>
    /// <param name="problem">The problem, in words for the user.</param>
    void SetProblem(string? problem);
}

/// <summary>
/// The source of the events of a trigger. A polled source answers <see cref="ReadAsync"/>; a pushed one runs
/// <see cref="ListenAsync"/> while the trigger is armed. The host calls a source from the thread pool, with a time limit, and a
/// source that throws is retried later: its automation says what failed.
/// </summary>
public interface IAutomationTriggerSource
{
    /// <summary>Gets how the source gives its events.</summary>
    PluginTriggerDelivery Delivery { get; }

    /// <summary>Checks the settings of a trigger, beyond what its fields say.</summary>
    /// <param name="context">The trigger.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>What is wrong with them, in a sentence, or null.</returns>
    ValueTask<string?> ValidateAsync(PluginTriggerContext context, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    /// <summary>Reads what happened since a cursor.</summary>
    /// <param name="context">The trigger.</param>
    /// <param name="cursor">
    /// The cursor of the previous answer; null the first time, which answers where the trigger starts and no event, since what is
    /// there already is not an event.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The events and the next cursor.</returns>
    /// <exception cref="NotSupportedException">The source is not <see cref="PluginTriggerDelivery.Polled"/>.</exception>
    ValueTask<PluginTriggerPage> ReadAsync(PluginTriggerContext context, string? cursor, CancellationToken cancellationToken)
        => throw new NotSupportedException("This trigger source is not polled.");

    /// <summary>Listens for the events of a trigger until the token is canceled, and hands each one to the sink.</summary>
    /// <param name="context">The trigger.</param>
    /// <param name="sink">Where the events go.</param>
    /// <param name="cancellationToken">Canceled when the trigger is disarmed: the automation is turned off, paused, edited or removed.</param>
    /// <returns>A task that ends when the listener stops; one that ends before the token is canceled is started again after a pause.</returns>
    /// <exception cref="NotSupportedException">The source is not <see cref="PluginTriggerDelivery.Pushed"/>.</exception>
    Task ListenAsync(PluginTriggerContext context, IPluginTriggerSink sink, CancellationToken cancellationToken)
        => throw new NotSupportedException("This trigger source does not listen.");
}
