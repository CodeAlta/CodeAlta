using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.CodeMode;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// Runs the <c>alta</c> calls of a script: the command line the binding builds, dispatched with the identity of the
/// session that runs the script, its run and the step of the call. Every gate of <c>alta</c> keys on that identity,
/// so a script reaches exactly what its session reaches. In a session, each call is a call of the session's own
/// <c>alta</c> tool, so what the session puts around its tools (hooks, the record of its calls) is around these too.
/// </summary>
public sealed class AltaScriptCallHandler : ICodeModeCallHandler
{
    private readonly AltaCommandDispatcher _dispatcher;
    private readonly AltaScriptBinding _binding;
    private readonly AltaCallerIdentity _caller;
    private readonly string? _cwd;
    private readonly IAgentRunToolInvoker? _tools;

    /// <summary>Creates the handler of one run, whose calls go to the dispatcher.</summary>
    /// <param name="dispatcher">The dispatcher of the host.</param>
    /// <param name="binding">The binding of the commands the caller gets.</param>
    /// <param name="caller">The identity of the session that runs the script, with its run.</param>
    /// <param name="cwd">The folder of the calls.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/>, <paramref name="binding"/> or <paramref name="caller"/> is null.</exception>
    public AltaScriptCallHandler(AltaCommandDispatcher dispatcher, AltaScriptBinding binding, AltaCallerIdentity caller, string? cwd)
        : this(dispatcher, binding, caller, cwd, null)
    {
    }

    /// <summary>Creates the handler of one run of a session, whose calls go through the <c>alta</c> tool of that session.</summary>
    /// <param name="dispatcher">The dispatcher of the host, for a caller without tools.</param>
    /// <param name="binding">The binding of the commands the caller gets.</param>
    /// <param name="caller">The identity of the session that runs the script, with its run.</param>
    /// <param name="cwd">The folder of the calls.</param>
    /// <param name="tools">
    /// The tools of the session, or null. With them, each call is a call of its <c>alta</c> tool: the hooks of the session
    /// check it, and it is recorded, as a call of the model is.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/>, <paramref name="binding"/> or <paramref name="caller"/> is null.</exception>
    public AltaScriptCallHandler(AltaCommandDispatcher dispatcher, AltaScriptBinding binding, AltaCallerIdentity caller, string? cwd, IAgentRunToolInvoker? tools)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(caller);
        _dispatcher = dispatcher;
        _binding = binding;
        _caller = caller;
        _cwd = cwd;
        _tools = tools;
    }

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_binding.TryCreateCommandLine(call.Path, call.ArgumentsJson, out var commandLine, out var error))
        {
            return CodeModeCallResult.Failure(error!);
        }

        AltaCommandResult result;
        if (_tools is null)
        {
            result = await _dispatcher.InvokeRawAsync(commandLine!.Arguments, commandLine.Stdin, _caller with { ScriptStepId = call.StepId }, _cwd, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var answer = await _tools.InvokeAsync(new AgentNestedToolCall(AltaSessionToolFactory.ToolName, ToolArguments(commandLine!))
            {
                StepId = call.StepId,
                ScriptRunId = _caller.ScriptRunId,
            }, cancellationToken).ConfigureAwait(false);
            // A call that did not reach the command (a hook that blocks it, a tool that is refused) fails as a tool does.
            if (answer.Structured is not { ValueKind: JsonValueKind.Object } written || !written.TryGetProperty(AltaSessionToolFactory.ScriptExitCode, out var exitCode))
            {
                var said = string.Join('\n', answer.Items.OfType<AgentToolResultItem.Text>().Select(static text => text.Value)).Trim();
                return CodeModeCallResult.Failure(new CodeModeCallError("ToolError",
                    answer.Error ?? (!answer.Success && said.Length > 0 ? said : "The `alta` tool of this session did not answer the script with the output of the command."))
                {
                    Code = AltaSessionToolFactory.ToolName,
                });
            }

            result = new AltaCommandResult
            {
                ExitCode = exitCode.GetInt32(),
                Stdout = written.GetProperty(AltaSessionToolFactory.ScriptStdout).GetString() ?? string.Empty,
                Stderr = written.GetProperty(AltaSessionToolFactory.ScriptStderr).GetString() ?? string.Empty,
                Error = answer.Error,
                CorrelationId = string.Empty,
                Duration = TimeSpan.Zero,
            };
        }

        return commandLine!.Raw ? AltaScriptBinding.ReadRun(result) : AltaScriptBinding.ReadResult(result, commandLine.CommandPath);
    }

    private static JsonElement ToolArguments(AltaScriptCommandLine commandLine)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("args");
            foreach (var argument in commandLine.Arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            if (commandLine.Stdin is { } stdin)
            {
                writer.WriteString("stdin", stdin);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
