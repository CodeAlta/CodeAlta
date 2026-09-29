using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

internal sealed class DesktopRpcDiagnostics : INeoRpcDiagnosticSink
{
    public void Write(NeoRpcDiagnostic diagnostic)
    {
        if (diagnostic.Level < NeoRpcDiagnosticLevel.Warning) return;
        // Framework codes only: no request arguments, prompt text or credentials.
        LogManager.GetLogger("CodeAlta.Desktop.Rpc").Warn($"RPC diagnostic: {diagnostic.Code} ({diagnostic.CorrelationId ?? "none"})");
    }
}
