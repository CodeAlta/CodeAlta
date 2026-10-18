using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// The decision providers of the window (<c>type = "typesafe"</c>): the service `alta decide` and the Test of the
/// Providers page consult, and the HTTP client it owns until the application ends.
/// </summary>
internal sealed class DesktopDecisions : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public DesktopDecisions(CodeAltaConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        Service = new ConfiguredAltaDecisionService(configStore, _http);
    }

    public IAltaDecisionService Service { get; }

    public void Dispose() => _http.Dispose();
}
