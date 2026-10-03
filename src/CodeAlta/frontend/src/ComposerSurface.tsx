import type { ComponentProps, ReactNode } from "react";
import { Button, ButtonGroup, FormGroup, Menu, MenuItem, Popover, Slider } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
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

/** Display names of the current next-Send selection, shown on the collapsed chip. */
export type ComposerSelectionSummary = Readonly<{ agent: string; provider: string; model: string; reasoning: string }>;

// Both draft and owned composers present their selection as one chip that opens this form.
// Supplied controls retain their own catalog/selection authority; `locked` keeps the form
// open while a control owns an admitted change (a provider switch) that must not be orphaned.
export function ComposerSelectionFields({ sessionId, onOpenCatalog, agent, provider, model, reasoning, summary, locked = false }: {
  sessionId: string; onOpenCatalog?: (page: "prompts" | "models") => void;
  agent: ReactNode; provider: ReactNode; model: ReactNode; reasoning: ReactNode;
  summary: ComposerSelectionSummary; locked?: boolean;
}) {
  const { t } = useShellLanguage();
  const form = <div className="composer-selection-form" role="group" aria-label={t("Session configuration")}>
    <FormGroup label={t("Agent prompt")} labelFor={`composer-agent-${sessionId}`}>{agent}</FormGroup>
    <FormGroup label={t("Provider")}>{provider}</FormGroup>
    <FormGroup label={t("Model")} labelFor={`composer-model-${sessionId}`}>{model}</FormGroup>
    <FormGroup label={t("Reasoning")}>{reasoning}</FormGroup>
    <div className="composer-selection-links">
      <Button variant="minimal" size="small" icon={<AppIcon name="assistant" size={14} />} onClick={() => onOpenCatalog?.("prompts")}>{t("Browse agent prompts")}</Button>
      <Button variant="minimal" size="small" icon={<AppIcon name="model" size={14} />} onClick={() => onOpenCatalog?.("models")}>{t("Browse models")}</Button>
    </div>
  </div>;
  return <Popover content={form} placement="top-start" popoverClassName="composer-selection-popover"
    canEscapeKeyClose={!locked} onInteraction={(next, event) => { if (!next && locked) event?.preventDefault(); }}
    {...(locked ? { isOpen: true } : {})}>
    <Button variant="minimal" className="composer-selection" aria-label={t("Session configuration")}
      title={t("Agent, model and reasoning for the next Send")} endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part"><AppIcon name="assistant" size={14} /><span>{summary.agent}</span></span>
      <span className="composer-selection-part"><AppIcon name="model" size={14} /><span>{summary.provider}</span>
        <span className="composer-selection-separator" aria-hidden="true">/</span><span>{summary.model}</span></span>
      <span className="composer-selection-part"><AppIcon name="brain" size={14} /><span>{summary.reasoning}</span></span>
    </Button>
  </Popover>;
}

/** Stepped reasoning-effort slider: the first stop is the model default, then the model's supported efforts. */
export function ReasoningSlider({ value, efforts, disabled = false, onChange }: {
  value: string | null; efforts: readonly string[]; disabled?: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  // A saved effort the selected model does not report stays visible as its own stop.
  const stops = ["", ...efforts, ...(value && !efforts.includes(value) ? [value] : [])];
  if (stops.length === 1) return <span className="composer-reasoning-static">{t("Model default")}</span>;
  const index = Math.max(0, stops.indexOf(value ?? ""));
  return <div className="composer-reasoning-slider" data-stops={stops.length}>
    <Slider min={0} max={stops.length - 1} stepSize={1} labelStepSize={1} value={index} disabled={disabled}
      handleHtmlProps={{ "aria-label": t("Reasoning") }}
      labelRenderer={stop => stops[stop] === "" ? t("Default") : efforts.includes(stops[stop]) ? stops[stop] : `${stops[stop]} · ${t("Unverified")}`}
      onChange={next => { if (next !== index) onChange(stops[next]); }} />
  </div>;
}

/** One send slot: the primary button performs the chosen default action and the caret chooses it. */
export function SendSplitButton({ enqueue, onEnqueueChange, enqueueDisabled = false, optionsDisabled = false, children }: {
  enqueue: boolean; onEnqueueChange: (value: boolean) => void; enqueueDisabled?: boolean; optionsDisabled?: boolean; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const menu = <Menu aria-label={t("Send options")}>
    <MenuItem roleStructure="listoption" selected={!enqueue} icon={<AppIcon name="send" size={16} />} text={t("Send now")}
      label="Enter" title={t("Sent immediately.")} onClick={() => onEnqueueChange(false)} />
    <MenuItem roleStructure="listoption" selected={enqueue} disabled={enqueueDisabled} icon={<AppIcon name="queue" size={16} />}
      text={t("Enqueue until idle")} title={t("Sent as soon as the session is idle.")} onClick={() => onEnqueueChange(true)} />
  </Menu>;
  return <ButtonGroup className="composer-send-group">
    {children}
    <Popover content={menu} placement="top-end" disabled={optionsDisabled}>
      <Button intent="primary" className="composer-send-options" disabled={optionsDisabled} aria-label={t("Send options")}
        title={t("Send options")} icon={<AppIcon name="chevronDown" size={14} />} />
    </Popover>
  </ButtonGroup>;
}

export function ComposerToolbar({ options, children }: { options?: ReactNode; children: ReactNode }) {
  const { t } = useShellLanguage();
  return <div className="composer-toolbar">
    {options && <div className="prompt-options" aria-label={t("Session configuration")}>{options}</div>}
    <div className="history-controls composer-actions">{children}</div>
  </div>;
}
