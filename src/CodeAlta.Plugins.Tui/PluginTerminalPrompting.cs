using CodeAlta.Plugins.Abstractions;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins.Tui;

/// <summary>Optional terminal anchor capability for plugin-owned prompt editor attachments.</summary>
public interface IPluginTerminalPromptEditorHost : IPluginPromptEditorHost
{
    /// <summary>Gets the editor visual used as an anchor for terminal-owned UI.</summary>
    Visual Visual { get; }
}
