import { Tag } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/**
 * Says that something comes from the layout of GitHub Copilot: a skill, an agent, instructions or an MCP server
 * that CodeAlta reads where Copilot keeps them (`.github`, `~/.copilot`). `title` says where.
 */
export function CopilotTag({ title }: { title?: string }) {
  const { t } = useShellLanguage();
  return <Tag minimal round className="copilot-tag" icon={<AppIcon name="github" size={12} />} title={title ?? t("From the GitHub Copilot layout")}>Copilot</Tag>;
}

/** The scopes of agent prompts that are custom agents of GitHub Copilot, as the host names them. */
export const copilotPromptScope = (scope: string) => scope === "CopilotProject" || scope === "CopilotGlobal";

/** The sources of skills that are folders of GitHub Copilot, as the host names them. */
export const copilotSkillSource = (source: string) => source === "ProjectCopilot" || source === "UserCopilot";
