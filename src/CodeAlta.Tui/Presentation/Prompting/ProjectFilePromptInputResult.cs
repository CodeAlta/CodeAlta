using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed record ProjectFilePromptInputResult(
    string NormalizedPromptText,
    AgentInput Input,
    IReadOnlyList<ProjectFileResolution> ResolvedReferences);
