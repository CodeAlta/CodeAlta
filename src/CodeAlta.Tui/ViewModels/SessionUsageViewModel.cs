using CodeAlta.Agent;
using CodeAlta.Tui.App;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.ViewModels;

internal sealed partial class SessionUsageViewModel
{
    public SessionUsageViewModel()
    {
        ProviderName = string.Empty;
        PluginTransientEvents = [];
    }

    [Bindable]
    public partial AgentSessionUsage? Usage { get; set; }

    [Bindable]
    public partial string ProviderName { get; set; }

    [Bindable]
    public partial string? ModelName { get; set; }

    [Bindable]
    public partial IReadOnlyList<PluginTransientEventProjection> PluginTransientEvents { get; set; }
}
