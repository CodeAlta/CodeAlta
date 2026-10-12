import { useEffect, useMemo, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { highlightCode } from "./codeHighlight";
import { useShellLanguage } from "./shellLanguage";
import type { ToolState } from "./toolCall";

/** The shell language a command of this system is written in, for its colors. */
export const shellLanguage = typeof navigator !== "undefined" && navigator.platform.startsWith("Win") ? "powershell" : "bash";

/** How far a call is, as a tag: its tile and its details say it the same way. */
export function ToolStateTag({ state }: { state: ToolState }) {
  const { t } = useShellLanguage();
  return <Tag minimal round className="tool-state" data-state={state}
    intent={state === "running" ? "primary" : state === "completed" ? "success" : state === "failed" ? "danger" : "none"}
    icon={state === "running" ? <ActivitySpinner size={11} /> : undefined}>
    {t(state === "running" ? "Running" : state === "completed" ? "Completed" : state === "failed" ? "Failed" : state === "canceled" ? "Canceled" : "Pending")}
  </Tag>;
}

/** Copies a text; the icon confirms it for a moment. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1600);
    return () => clearTimeout(timer);
  }, [copied]);
  return <Button variant="minimal" size="small" className="tool-copy" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
    aria-label={label} title={copied ? t("Copied") : label}
    onClick={() => void navigator.clipboard?.writeText(text).then(() => setCopied(true), () => { /* Nothing is copied. */ })} />;
}

/** The command a call runs, as it is typed at a prompt. */
export function CommandBlock({ command, language = shellLanguage }: { command: string; language?: string }) {
  const { t } = useShellLanguage();
  const html = useMemo(() => highlightCode(command, language), [command, language]);
  return <div className="tool-command">
    <span className="tool-command-prompt" aria-hidden="true">❯</span>
    <pre className="tool-command-text" tabIndex={0} aria-label={t("Command")}>{html !== null ? <code dangerouslySetInnerHTML={{ __html: html }} /> : <code>{command}</code>}</pre>
    <CopyButton text={command} label={t("Copy command")} />
  </div>;
}
