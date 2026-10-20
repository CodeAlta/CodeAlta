import type { WorkspaceSession } from "#neoastra";
import type { MessageKey } from "../localization";
import type { SessionMenuEntry } from "../SessionTabMenu";
import { opensElsewhere, type OpenElsewhereAction } from "./claudeCodeOpen";

/** Which of the actions are allowed for the session of a menu (`sessionActionAccess`). */
export type OpenElsewhereAccess = Readonly<Partial<Record<OpenElsewhereAction, boolean>>>;

/**
 * The lines of the menu of a session that open it in VS Code or a terminal: only for a session of a Claude Code
 * provider that has a conversation of the CLI. A line that is not allowed now is shown disabled.
 */
export function openElsewhereEntries(t: (key: MessageKey) => string, session: Readonly<Pick<WorkspaceSession, "providerConversation">>,
  providerType: string | null | undefined, access: OpenElsewhereAccess, onAction: (action: OpenElsewhereAction) => void): SessionMenuEntry[] {
  if (!opensElsewhere(session, providerType)) return [];
  return [
    { key: "open-vs-code", label: t("Open in VS Code"), icon: "openExternal", disabled: !access["open-vs-code"], onSelect: () => onAction("open-vs-code") },
    { key: "run-in-terminal", label: t("Run in terminal"), icon: "terminal", disabled: !access["run-in-terminal"], onSelect: () => onAction("run-in-terminal") },
    { key: "copy-resume-command", label: t("Copy resume command"), icon: "copy", disabled: !access["copy-resume-command"], onSelect: () => onAction("copy-resume-command") },
  ];
}
