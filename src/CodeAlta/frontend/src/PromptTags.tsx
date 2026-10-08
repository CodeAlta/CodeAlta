import type { ReactNode } from "react";
import { Tag } from "@blueprintjs/core";
import type { AgentPromptEntry } from "#neoastra";
import { CopilotTag, copilotPromptScope } from "./CopilotTag";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";

// Where a prompt is kept, as its tag says it. A custom agent of GitHub Copilot is of the user or of the project too.
const scopeLabel = (scope: string): MessageKey => scope === "BuiltIn" ? "Built-in" : scope === "Project" || scope === "CopilotProject" ? "Project" : "Global";

/**
 * The tags of a prompt in the list of Settings: where it is kept, the mark of GitHub Copilot for one of its custom
 * agents, and whether another prompt replaces it. The actions of the row come after them.
 */
export function PromptTags({ prompt, children }: { prompt: Pick<AgentPromptEntry, "scope" | "shadowed">; children?: ReactNode }) {
  const { t } = useShellLanguage();
  const copilot = copilotPromptScope(prompt.scope);
  return <span className="settings-editor-tags"><Tag minimal round intent={prompt.scope === "BuiltIn" || copilot ? "none" : "primary"}>{t(scopeLabel(prompt.scope))}</Tag>
    {copilot && <CopilotTag />}
    {prompt.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}{children}</span>;
}
