namespace CodeAlta.LiveTool.Scripts;

/// <summary>The kind of the value of an option or an argument of an <c>alta</c> command, for the scripts of Code Mode.</summary>
public enum AltaValueKind
{
    /// <summary>A string.</summary>
    String,

    /// <summary>An integer.</summary>
    Integer,

    /// <summary>A number.</summary>
    Number,

    /// <summary>One of the values of <see cref="AltaValue.Values"/>.</summary>
    Enum,

    /// <summary>A JSON value, written as JSON text on the command line.</summary>
    Json,

    /// <summary>A list of strings, written as one option per value.</summary>
    StringList,

    /// <summary>A list of strings, written as one value separated by commas.</summary>
    CommaList,
}

/// <summary>The type of the value of an option or an argument.</summary>
/// <param name="Kind">The kind of the value.</param>
public sealed record AltaValue(AltaValueKind Kind)
{
    /// <summary>Gets a string.</summary>
    public static AltaValue String { get; } = new(AltaValueKind.String);

    /// <summary>Gets an integer.</summary>
    public static AltaValue Integer { get; } = new(AltaValueKind.Integer);

    /// <summary>Gets a number.</summary>
    public static AltaValue Number { get; } = new(AltaValueKind.Number);

    /// <summary>Gets a JSON value.</summary>
    public static AltaValue Json { get; } = new(AltaValueKind.Json);

    /// <summary>Gets a list of strings, one option per value.</summary>
    public static AltaValue StringList { get; } = new(AltaValueKind.StringList);

    /// <summary>Gets a list of strings separated by commas.</summary>
    public static AltaValue CommaList { get; } = new(AltaValueKind.CommaList);

    /// <summary>Gets the values of an enum.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Creates an enum.</summary>
    /// <param name="values">Its values.</param>
    /// <returns>The type.</returns>
    public static AltaValue Enum(params string[] values) => new(AltaValueKind.Enum) { Values = values };
}

/// <summary>What the <c>--stdin</c> of a command reads.</summary>
public enum AltaStdinKind
{
    /// <summary>The command takes no stdin.</summary>
    None,

    /// <summary>Text, such as a prompt or Markdown.</summary>
    Text,

    /// <summary>A JSON value: a script passes an object, which is serialized.</summary>
    Json,
}

/// <summary>
/// What the command tree does not say about a command and a script needs to know: the types of its values, the
/// options it requires, what its stdin reads, and the TypeScript type of what it answers.
/// </summary>
public sealed record AltaCommandAnnotation
{
    /// <summary>Gets the types of options and arguments, by their name on the command line (<c>project</c>, <c>session-id</c>).</summary>
    public IReadOnlyDictionary<string, AltaValue> Values { get; init; } = new Dictionary<string, AltaValue>(StringComparer.Ordinal);

    /// <summary>Gets the options the command requires, by their name on the command line.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>Gets the defaults of options, as text for their description, by their name on the command line.</summary>
    public IReadOnlyDictionary<string, string> Defaults { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets what <c>--stdin</c> reads, and its description.</summary>
    public AltaStdinKind Stdin { get; init; }

    /// <summary>Gets a description of what stdin holds, shown on the <c>stdin</c> property.</summary>
    public string? StdinDescription { get; init; }

    /// <summary>
    /// Gets the TypeScript type of what a script receives: the main record when the command answers one record (a
    /// name declared in <see cref="AltaScriptAnnotations.Types"/>, such as <c>SessionCreated</c>), else a list.
    /// </summary>
    public string? Result { get; init; }
}
