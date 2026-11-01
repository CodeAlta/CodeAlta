using System.Reflection;
using System.Text.Json;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Agent;

/// <summary>The in-page agent of the browser tools, and the functions the drivers call it with.</summary>
internal static class BrowserAgent
{
    /// <summary>The name of the isolated world (CDP) or sandbox realm (BiDi) the agent runs in.</summary>
    public const string WorldName = "codealta";

    /// <summary>What a call answers in a world that has no agent yet.</summary>
    public const string Missing = "codealta-agent-missing";

    /// <summary>Gets the source of the agent, which installs itself once in a world.</summary>
    public static string Source { get; } = Load();

    /// <summary>The function that runs an operation: its arguments are the name and the options.</summary>
    public const string CallFunction =
        "function (name, options) { const agent = globalThis.__codealtaBrowserAgent; return agent ? agent.call(name, options) : '" + Missing + "'; }";

    /// <summary>The function that gives an element of the latest snapshot: its argument is the uid.</summary>
    public const string ElementFunction =
        "function (uid) { const agent = globalThis.__codealtaBrowserAgent; if (!agent) throw new Error('No snapshot found. Use take_snapshot to capture one.'); return agent.element(uid); }";

    /// <summary>The function that gives the file input of an element: itself, one inside it, or the control of its label.</summary>
    public const string FileInputFunction =
        "function (uid) { const agent = globalThis.__codealtaBrowserAgent; if (!agent) throw new Error('No snapshot found. Use take_snapshot to capture one.'); " +
        "let element = agent.element(uid); if (element.localName === 'input' && element.type === 'file') return element; " +
        "if (element.localName === 'label' && element.control && element.control.type === 'file') return element.control; " +
        "return element.querySelector('input[type=file]'); }";

    /// <summary>The function that gives the window of a frame element: its argument is the uid.</summary>
    public const string FrameWindowFunction =
        "function (uid) { const agent = globalThis.__codealtaBrowserAgent; if (!agent) throw new Error('No snapshot found. Use take_snapshot to capture one.'); return agent.frameWindow(uid); }";

    /// <summary>
    /// Wraps the function of a caller so that it runs with its arguments and gives its result as JSON text, as
    /// <c>JSON.stringify</c> writes it. The function is part of the code, not a string in it.
    /// </summary>
    public static string WrapFunction(string source)
        => "async function (...args) { const fn = (\n" + source + "\n); const value = await fn(...args); " +
           "if (value === undefined) return { undefined: true }; const json = JSON.stringify(value); return json === undefined ? { undefined: true } : { json: json }; }";

    /// <summary>Reads the answer of <see cref="CallFunction"/>: the value, or the failure of the operation.</summary>
    /// <returns>The value, or null when the world has no agent yet.</returns>
    /// <exception cref="BrowserToolException">The operation failed.</exception>
    public static JsonElement? ReadAnswer(string? text)
    {
        if (text is null || text == Missing) return null;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return root.TryGetProperty("value", out var value) ? value.Clone() : default(JsonElement);
        }

        var error = root.GetProperty("error");
        throw new BrowserToolException(error.TryGetProperty("code", out var code) ? code.GetString() ?? "error" : "error", error.GetProperty("message").GetString() ?? "The page refused the operation.");
    }

    /// <summary>The message of an error a script threw: its first line, without the name of its type.</summary>
    public static string ErrorText(string? description, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(description) ? fallback : description;
        var line = text.Split('\n')[0].Trim();
        return line.StartsWith("Error: ", StringComparison.Ordinal) ? line[7..] : line;
    }

    private static string Load()
    {
        using var stream = typeof(BrowserAgent).Assembly.GetManifestResourceStream("CodeAlta.Browser.browser-agent.js")
            ?? throw new InvalidOperationException("The browser agent is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
