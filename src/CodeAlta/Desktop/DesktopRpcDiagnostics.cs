using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

internal sealed class DesktopRpcDiagnostics : INeoRpcDiagnosticSink
{
    public void Write(NeoRpcDiagnostic diagnostic)
    {
        if (diagnostic.Level < NeoRpcDiagnosticLevel.Warning) return;
        // The framework's code and its own fixed description: no request arguments, prompt text or credentials.
        var logger = LogManager.GetLogger("CodeAlta.Desktop.Rpc");
        var text = $"RPC diagnostic: {diagnostic.Code} ({diagnostic.CorrelationId ?? "none"}) {diagnostic.Message}";
        if (diagnostic.Level >= NeoRpcDiagnosticLevel.Error) logger.Error(text);
        else logger.Warn(text);
    }
}

/// <summary>
/// Logs the exception of an RPC command that failed, with the correlation id the page and the diagnostic line show: the
/// framework answers <c>internal_error</c> and keeps the exception host-side, so without this line nothing says what
/// failed. It maps nothing: the answer stays the framework's.
/// </summary>
internal sealed class DesktopRpcExceptionLog : INeoRpcErrorMapper
{
    /// <inheritdoc />
    public bool TryMap(Exception exception, NeoRpcContext context, out NeoRpcError error)
    {
        error = default!;
        // A canceled command is answered as such and is not a failure; an explicit error is the command's own answer.
        if (exception is OperationCanceledException or NeoRpcException) return false;
        try
        {
            LogManager.GetLogger("CodeAlta.Desktop.Rpc").Error(exception, $"RPC command failed ({context.CorrelationId}).");
        }
        catch
        {
            // Logging never changes the answer of a command.
        }

        return false;
    }
}
