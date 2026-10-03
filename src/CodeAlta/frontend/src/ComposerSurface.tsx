import type { ComponentProps, ReactNode } from "react";
import { Button, FormGroup } from "@blueprintjs/core";
import { PromptEditor } from "./PromptEditor";
import { useShellLanguage } from "./shellLanguage";

// Shared presentation for owned, new-session and catalog composers. Authority,
// request recovery and draft ownership stay with each composer, not this surface.
export function ComposerSurface({ status, busy = false, children, className, editor, expandedEditor, notice, options }: {
  status: ReactNode; busy?: boolean; children: ReactNode; className?: string;
  editor: ComponentProps<typeof PromptEditor>; expandedEditor?: ReactNode; notice?: ReactNode; options?: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <section className={`owned-session${className ? ` ${className}` : ""}`} aria-label={t("Message composer")}>
    <div className="composer-status-line" role="status" data-busy={busy}><span>{status}</span></div>
    {expandedEditor}
    {notice}
    <label className="sr-only" htmlFor={editor.id}>{t("Message")}</label>
    <PromptEditor {...editor} />
    <ComposerToolbar options={options}>{children}</ComposerToolbar>
  </section>;
}

// Both draft and owned composers use exactly these field wrappers and toolbar rows.
// Supplied controls retain their own catalog/selection authority.
export function ComposerSelectionFields({ sessionId, onOpenCatalog, agent, provider, model, reasoning }: {
  sessionId: string; onOpenCatalog?: (page: "prompts" | "models") => void;
  agent: ReactNode; provider: ReactNode; model: ReactNode; reasoning: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <>
    <FormGroup className="composer-field" label={<Button variant="minimal" onClick={() => onOpenCatalog?.("prompts")}>{t("Agent→")}</Button>}
      labelFor={`composer-agent-${sessionId}`}>{agent}</FormGroup>
    <FormGroup className="composer-field composer-model-field" label={<Button variant="minimal" onClick={() => onOpenCatalog?.("models")}>{t("Model→")}</Button>}
      labelFor={`composer-model-${sessionId}`}><div className="composer-model-options">{provider}{model}</div></FormGroup>
    <FormGroup className="composer-field" labelFor={`composer-reasoning-${sessionId}`}>{reasoning}</FormGroup>
  </>;
}

export function ComposerToolbar({ options, children }: { options?: ReactNode; children: ReactNode }) {
  const { t } = useShellLanguage();
  return <div className="composer-toolbar">
    {options && <div className="prompt-options" aria-label={t("Session configuration")}>{options}</div>}
    <div className="history-controls composer-actions">{children}</div>
  </div>;
}
