import type { SessionClaudeCodeConversationResult } from "#neoastra";
import { Button } from "@blueprintjs/core";
import { useEffect, useState } from "react";
import { AppIcon } from "../AppIcon";
import { originKey } from "../conversationHeld/conversationHeld";
import { useShellLanguage } from "../shellLanguage";

/**
 * The row of the Session Info window that names the conversation of the Claude Code CLI a session continues, with a
 * button that copies its identifier. Read once when the window opens; nothing is shown for a session that has none.
 */
export function ClaudeCodeConversationRow({ read, write = navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined }: {
  read: () => Promise<SessionClaudeCodeConversationResult | null>;
  write?: (text: string) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const [conversation, setConversation] = useState<SessionClaudeCodeConversationResult | null>(null);
  const [copied, setCopied] = useState<"copied" | "failed" | null>(null);
  useEffect(() => {
    let alive = true;
    setConversation(null);
    setCopied(null);
    void read().then(value => { if (alive) setConversation(value); }, () => { /* Nothing to show. */ });
    return () => { alive = false; };
  }, [read]);
  const id = conversation?.cliSessionId;
  if (!id) return null;
  const held = conversation.held;
  const where = held ? originKey(held.origin, held.originText) : null;
  return <div className="claude-code-conversation-row"><dt>{t("Claude Code conversation")}</dt><dd>
    <code>{id}</code>{" "}
    <Button variant="minimal" size="small" icon={<AppIcon name="copy" size={14} />} disabled={!write}
      aria-label={t("Copy the identifier of the Claude Code conversation")} title={t("Copy the identifier of the Claude Code conversation")}
      text={t(copied === "copied" ? "Copied" : "Copy")}
      onClick={() => void write?.(id).then(() => setCopied("copied"), () => setCopied("failed"))} />
    {copied === "failed" && <small role="alert">{t("Clipboard unavailable; nothing copied.")}</small>}
    {held && <small>{t("Open now in {where}", { where: where ? t(where) : held.originText })}</small>}
  </dd></div>;
}
