using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// The tools of a run as functions of its scripts: <c>tools.read_file(args)</c> for the session's tools and the tools of
/// plugins, <c>mcp.&lt;server&gt;.&lt;tool&gt;(args)</c> for the tools of MCP servers. A call runs through the run's own
/// handler (<see cref="IAgentRunToolInvoker"/>), with its permissions and its sandbox.
/// </summary>
public sealed class ToolScriptBinding : ICodeModeCallHandler
{
    /// <summary>The namespace of the session's tools in scripts.</summary>
    public const string ToolsNamespace = "tools";

    /// <summary>The namespace of the tools of MCP servers in scripts.</summary>
    public const string McpNamespace = "mcp";

    private readonly IAgentRunToolInvoker _invoker;
    private readonly FrozenDictionary<string, string> _toolByPath;

    /// <summary>Creates the binding of the tools of a run.</summary>
    /// <param name="invoker">The tools of the run.</param>
    /// <param name="excluded">Tools scripts reach otherwise, such as <c>alta</c>, whose commands are functions of their own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="invoker"/> or <paramref name="excluded"/> is null.</exception>
    public ToolScriptBinding(IAgentRunToolInvoker invoker, IEnumerable<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(excluded);
        _invoker = invoker;
        var skip = excluded.ToHashSet(StringComparer.Ordinal);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var functions = new List<CodeModeFunction>();
        var idempotent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in invoker.ListTools())
        {
            if (skip.Contains(spec.Name) || spec.SearchesDeferredTools)
            {
                continue;
            }

            var path = spec.Group is { Length: > 0 } server
                ? $"{McpNamespace}.{CodeModeNames.ToIdentifier(server)}.{CodeModeNames.ToIdentifier(spec.ScriptName ?? spec.Name)}"
                : $"{ToolsNamespace}.{CodeModeNames.ToIdentifier(spec.Name)}";
            if (!byPath.TryAdd(path, spec.Name))
            {
                continue;
            }

            if (spec.Idempotent)
            {
                idempotent.Add(path);
            }

            functions.Add(new CodeModeFunction(path)
            {
                Description = spec.Description,
                InputSchemaJson = spec.InputSchema.ValueKind == JsonValueKind.Object ? spec.InputSchema.GetRawText() : null,
                OutputSchemaJson = spec.OutputSchema?.GetRawText(),
                // A tool of an MCP server without a schema gives its structured content when it has one, else its text.
                ResultType = spec.OutputSchema is not null ? null : spec.Group is { Length: > 0 } ? "unknown" : "string",
                Parallel = spec.RunsInParallel,
                TextArgument = spec.FreeformInput?.ArgumentName,
            });
        }

        _toolByPath = byPath.ToFrozenDictionary(StringComparer.Ordinal);
        _idempotent = idempotent.ToFrozenSet(StringComparer.Ordinal);
        Functions = functions;
    }

    private readonly FrozenSet<string> _idempotent;

    /// <summary>Gets whether a path is a tool that declares its calls idempotent (<see cref="AgentToolSpec.Idempotent"/>).</summary>
    /// <param name="path">The path of a function.</param>
    /// <returns>True when it is one.</returns>
    public bool IsIdempotent(string path) => _idempotent.Contains(path);

    /// <summary>Gets the functions, one per tool.</summary>
    public IReadOnlyList<CodeModeFunction> Functions { get; }

    /// <summary>Gets whether a path is a tool of this binding.</summary>
    /// <param name="path">The path of a function.</param>
    /// <returns>True when it is one.</returns>
    public bool Contains(string path) => _toolByPath.ContainsKey(path);

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_toolByPath.TryGetValue(call.Path, out var toolName))
        {
            return CodeModeCallResult.Failure(new CodeModeCallError("TypeError", $"`{call.Path}` is not a tool of this session."));
        }

        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var result = await _invoker.InvokeAsync(new AgentNestedToolCall(toolName, arguments.RootElement.Clone()) { StepId = call.StepId }, cancellationToken).ConfigureAwait(false);
        return Read(result, toolName);
    }

    /// <summary>
    /// Reads a tool result as a script receives it: its structured value when the tool gives one (also when the call
    /// failed, as a command that exits with an error code), else its text, else a <c>ToolError</c>.
    /// </summary>
    /// <param name="result">The result of the tool.</param>
    /// <param name="toolName">The name of the tool.</param>
    /// <returns>The outcome of the call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public static CodeModeCallResult Read(AgentToolResult result, string toolName)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Structured is { } structured)
        {
            return CodeModeCallResult.Success(structured.GetRawText());
        }

        var text = new StringBuilder();
        foreach (var item in result.Items)
        {
            var part = item switch
            {
                AgentToolResultItem.Text value => value.Value,
                AgentToolResultItem.Image image => $"[image {image.DisplayName ?? image.MediaType}]",
                AgentToolResultItem.LocalImage image => $"[image {image.DisplayName ?? image.Path}]",
                AgentToolResultItem.ImageUrl => "[image]",
                AgentToolResultItem.ToolReference reference => "Loaded tool: " + reference.ToolName,
                _ => null,
            };
            if (part is null)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(part);
        }

        return result.Success
            ? CodeModeCallResult.Text(text.ToString())
            : CodeModeCallResult.Failure(new CodeModeCallError("ToolError", result.Error ?? (text.Length > 0 ? text.ToString() : $"{toolName} failed.")) { Code = toolName });
    }
}
