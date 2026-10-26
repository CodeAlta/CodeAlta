using CodeAlta.CodeMode;

namespace CodeAlta.LiveTool.Scripts;

/// <summary>
/// Runs the <c>alta</c> calls of a script: the command line the binding builds, dispatched with the identity of the
/// session that runs the script, its run and the step of the call. Every gate of <c>alta</c> keys on that identity,
/// so a script reaches exactly what its session reaches.
/// </summary>
public sealed class AltaScriptCallHandler : ICodeModeCallHandler
{
    private readonly AltaCommandDispatcher _dispatcher;
    private readonly AltaScriptBinding _binding;
    private readonly AltaCallerIdentity _caller;
    private readonly string? _cwd;

    /// <summary>Creates the handler of one run.</summary>
    /// <param name="dispatcher">The dispatcher of the host.</param>
    /// <param name="binding">The binding of the commands the caller gets.</param>
    /// <param name="caller">The identity of the session that runs the script, with its run.</param>
    /// <param name="cwd">The folder of the calls.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatcher"/>, <paramref name="binding"/> or <paramref name="caller"/> is null.</exception>
    public AltaScriptCallHandler(AltaCommandDispatcher dispatcher, AltaScriptBinding binding, AltaCallerIdentity caller, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(caller);
        _dispatcher = dispatcher;
        _binding = binding;
        _caller = caller;
        _cwd = cwd;
    }

    /// <inheritdoc />
    public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_binding.TryCreateCommandLine(call.Path, call.ArgumentsJson, out var commandLine, out var error))
        {
            return CodeModeCallResult.Failure(error!);
        }

        var result = await _dispatcher.InvokeRawAsync(commandLine!.Arguments, commandLine.Stdin, _caller with { ScriptStepId = call.StepId }, _cwd, cancellationToken)
            .ConfigureAwait(false);
        return AltaScriptBinding.ReadResult(result, commandLine.CommandPath);
    }
}
