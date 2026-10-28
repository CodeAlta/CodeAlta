using System.Text.Json;

namespace CodeAlta.Agent;

/// <summary>
/// The raw-text form of the input of a tool whose input is one text, such as the source of a script or a patch: a provider
/// that can declare it (a custom tool of the OpenAI Responses API) has the model send that text as it is, without the
/// escapes of JSON, and the call reaches the handler as the JSON object <c>{"&lt;ArgumentName&gt;": "&lt;text&gt;"}</c>.
/// Every other provider declares the tool with its JSON schema, where <see cref="ArgumentName"/> is the property of that text.
/// </summary>
public sealed record AgentToolFreeformInput
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentToolFreeformInput"/> record.
    /// </summary>
    /// <param name="argumentName">The property of the JSON arguments that carries the text, such as <c>code</c>.</param>
    /// <param name="syntax">The syntax of <paramref name="definition"/>, or <see cref="AgentToolFreeformSyntax.Text"/> for any text.</param>
    /// <param name="definition">The grammar the text follows, in <paramref name="syntax"/>; null for plain text.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="argumentName"/> is empty, or when a grammar syntax has no definition.</exception>
    public AgentToolFreeformInput(string argumentName, AgentToolFreeformSyntax syntax = AgentToolFreeformSyntax.Text, string? definition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentName);
        if (syntax != AgentToolFreeformSyntax.Text && string.IsNullOrWhiteSpace(definition))
        {
            throw new ArgumentException($"A {syntax} grammar needs its definition.", nameof(definition));
        }

        ArgumentName = argumentName;
        Syntax = syntax;
        Definition = syntax == AgentToolFreeformSyntax.Text ? null : definition;
    }

    /// <summary>Gets the property of the JSON arguments that carries the text.</summary>
    public string ArgumentName { get; }

    /// <summary>Gets the syntax of <see cref="Definition"/>.</summary>
    public AgentToolFreeformSyntax Syntax { get; }

    /// <summary>Gets the grammar the text follows, or null for plain text.</summary>
    public string? Definition { get; }

    /// <summary>
    /// Gets the description of the tool in this form, which tells the model to send the text itself and not JSON; null to
    /// keep <see cref="AgentToolSpec.Description"/>.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Creates the JSON arguments a call in this form reaches its handler with.</summary>
    /// <param name="text">The text the model sent.</param>
    /// <returns>The object <c>{"&lt;ArgumentName&gt;": "&lt;text&gt;"}</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    public JsonElement CreateArguments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CreateArguments(ArgumentName, text);
    }

    /// <summary>Gets the text of JSON arguments of the tool, to send a recorded call in this form again.</summary>
    /// <param name="arguments">The JSON arguments of the call.</param>
    /// <param name="text">The text, when the arguments are an object whose <see cref="ArgumentName"/> is a string.</param>
    /// <returns>Whether the arguments hold the text.</returns>
    public bool TryGetText(JsonElement arguments, out string text)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(ArgumentName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Creates the JSON object <c>{"&lt;argumentName&gt;": "&lt;text&gt;"}</c>.</summary>
    /// <param name="argumentName">The name of the property.</param>
    /// <param name="text">The text.</param>
    /// <returns>The object.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="argumentName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is null.</exception>
    public static JsonElement CreateArguments(string argumentName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentName);
        ArgumentNullException.ThrowIfNull(text);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(argumentName, text);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}

/// <summary>
/// The syntax of the grammar of an <see cref="AgentToolFreeformInput"/>.
/// </summary>
public enum AgentToolFreeformSyntax
{
    /// <summary>Any text, without a grammar.</summary>
    Text,

    /// <summary>A grammar in the Lark syntax.</summary>
    Lark,

    /// <summary>A regular expression.</summary>
    Regex,
}
