using System.Text.Json;
using CodeAlta.CodeMode;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// <c>store(key, value)</c> and <c>load(key)</c> in scripts: JSON values a script keeps for the later scripts of its session
/// (<see cref="CodeModeValueStore"/>). In a host that keeps none (<c>altacli</c>), they last as long as the run. The prelude
/// gives scripts the functions; they call <c>codemode.store</c> and <c>codemode.load</c>, which this binding runs. A step a
/// resume replays does not run, so it does not store again.
/// </summary>
public sealed class CodeModeValueBinding : ICodeModeCallHandler
{
    /// <summary>The path of <c>store</c> in scripts.</summary>
    public const string StorePath = "codemode.store";

    /// <summary>The path of <c>load</c> in scripts.</summary>
    public const string LoadPath = "codemode.load";

    private readonly CodeModeValueStore? _store;
    private readonly string _sessionId;
    private readonly Dictionary<string, string> _run = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates the binding of one run.</summary>
    /// <param name="store">The values of the host, or null for values that last as long as the run.</param>
    /// <param name="sessionId">The session that runs the script.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    public CodeModeValueBinding(CodeModeValueStore? store, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        _store = store;
        _sessionId = sessionId;
    }

    /// <summary>Gets the paths this binding runs.</summary>
    public static IReadOnlyList<string> Paths { get; } = [StorePath, LoadPath];

    /// <summary>Gets the declarations of the functions.</summary>
    public static IReadOnlyList<CodeModeFunction> Functions { get; } =
    [
        new(StorePath)
        {
            Description = $"Keeps a JSON value under a key for the later scripts of this session; undefined removes it. Scripts call it as store(key, value). A value takes at most {CodeModeValueStore.MaxValueBytes / 1024} KB of JSON, the values of a session {CodeModeValueStore.MaxSessionBytes / (1024 * 1024)} MB.",
            InputSchemaJson = """{"type":"object","properties":{"key":{"type":"string"},"value":{"description":"Any JSON value; left out to remove the key."}},"required":["key"],"additionalProperties":false}""",
            ResultType = "void",
            Parallel = true,
        },
        new(LoadPath)
        {
            Description = "Reads the value a script of this session stored under a key, or undefined. Scripts call it as load(key).",
            InputSchemaJson = """{"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}""",
            ResultType = "unknown",
            Parallel = true,
        },
    ];

    /// <inheritdoc />
    public ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var arguments = document.RootElement;
        var key = arguments.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String ? keyElement.GetString() ?? string.Empty : string.Empty;
        try
        {
            if (call.Path == LoadPath)
            {
                string? found;
                if (_store is not null) found = _store.Load(_sessionId, key);
                else lock (_gate) found = _run.GetValueOrDefault(key);
                // { found, value }: a value that is JSON null is told apart from no value.
                return ValueTask.FromResult(CodeModeCallResult.Success(found is null ? """{"found":false}""" : $$"""{"found":true,"value":{{found}}}"""));
            }

            var value = arguments.TryGetProperty("value", out var valueElement) ? valueElement.GetRawText() : null;
            string? refusal;
            if (_store is not null)
            {
                refusal = _store.Store(_sessionId, key, value);
            }
            else
            {
                lock (_gate)
                {
                    refusal = CodeModeValueStore.Check(_run, key, value);
                    if (refusal is null)
                    {
                        if (value is null) _run.Remove(key); else _run[key] = value;
                    }
                }
            }

            return ValueTask.FromResult(refusal is null
                ? CodeModeCallResult.Success(null)
                : CodeModeCallResult.Failure(new CodeModeCallError("RangeError", refusal) { Code = "codemode.storeRefused" }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(CodeModeCallResult.Failure(new CodeModeCallError("ToolError", $"The values of this session cannot be read or written: {exception.Message}")));
        }
    }
}
