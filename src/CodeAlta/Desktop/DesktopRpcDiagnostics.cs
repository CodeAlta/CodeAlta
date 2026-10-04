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
