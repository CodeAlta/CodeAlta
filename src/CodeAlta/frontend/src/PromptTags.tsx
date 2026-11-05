import type { ReactNode } from "react";
import { Tag } from "@blueprintjs/core";
import type { AgentPromptEntry } from "#neoastra";
import { ClaudeTag, claudePromptScope } from "./ClaudeTag";
import { CopilotTag, copilotPromptScope } from "./CopilotTag";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";

// Where a prompt is kept, as its tag says it. An agent of GitHub Copilot or Claude Code is of the user or of the project too.
export const scopeLabel = (scope: string): MessageKey =>
  scope === "BuiltIn" ? "Built-in" : scope === "Project" || scope === "CopilotProject" || scope === "ClaudeProject" ? "Project" : "Global";

/**
 * The tags of a prompt in the list of Settings: where it is kept, the mark of GitHub Copilot or Claude Code for one of
 * their agents, and whether another prompt replaces it. The actions of the row come after them.
 */
export function PromptTags({ prompt, children }: { prompt: Pick<AgentPromptEntry, "scope" | "shadowed">; children?: ReactNode }) {
  const { t } = useShellLanguage();
  const copilot = copilotPromptScope(prompt.scope), claude = claudePromptScope(prompt.scope);
  return <span className="settings-editor-tags"><Tag minimal round intent={prompt.scope === "BuiltIn" || copilot || claude ? "none" : "primary"}>{t(scopeLabel(prompt.scope))}</Tag>
    {copilot && <CopilotTag />}{claude && <ClaudeTag />}
    {prompt.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}{children}</span>;
}
