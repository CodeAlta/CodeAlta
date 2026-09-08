using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Tui;

/// <summary>Optional terminal content for a dialog request; this does not imply host support.</summary>
public sealed record PluginTerminalDialogRequest : PluginDialogRequest
{
    /// <summary>Gets optional custom terminal content. Unsupported hosts need not consume it.</summary>
    public Visual? Content { get; init; }
}
