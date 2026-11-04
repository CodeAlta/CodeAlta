import { Tag } from "@blueprintjs/core";
import { BrandIcon } from "./BrandIcon";
import { useShellLanguage } from "./shellLanguage";

/** Says that an agent comes from the layout of Claude Code: a subagent CodeAlta reads where Claude Code keeps them (`.claude/agents`, `~/.claude/agents`). */
export function ClaudeTag({ title }: { title?: string }) {
  const { t } = useShellLanguage();
  return <Tag minimal round className="claude-tag" icon={<BrandIcon name="claudecode" size={12} />} title={title ?? t("From the Claude Code layout")}>Claude</Tag>;
}

/** The scopes of agent prompts that are subagents of Claude Code, as the host names them. */
export const claudePromptScope = (scope: string) => scope === "ClaudeProject" || scope === "ClaudeGlobal";
