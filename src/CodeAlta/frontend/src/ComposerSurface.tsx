import { useContext, type ComponentProps, type ReactNode, type SyntheticEvent } from "react";
import { Button, ButtonGroup, FormGroup, Menu, MenuItem, PopoverNext, Slider } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { modelBrand } from "./brands";
import { PermissionChipPart } from "./PermissionModeSelect";
import { ModelIcon, ProviderIcon, useProviderBrand } from "./ProviderIcon";
import { ComposerChrome } from "./composerChrome";
import { PromptEditor } from "./PromptEditor";
import { useShellLanguage } from "./shellLanguage";

// Shared presentation for owned, new-session and catalog composers. Authority,
// request recovery and draft ownership stay with each composer, not this surface.
export function ComposerSurface({ status, busy = false, children, className, editor, expandedEditor, notice, options }: {
  status: ReactNode; busy?: boolean; children: ReactNode; className?: string;
  editor: ComponentProps<typeof PromptEditor>; expandedEditor?: ReactNode; notice?: ReactNode; options?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const chrome = useContext(ComposerChrome);
  return <section className={`owned-session${className ? ` ${className}` : ""}`} aria-label={t("Message composer")}>
    {chrome.footer}
    <div className="composer-status-line" role="status" data-busy={busy}><span>{status}</span>{chrome.status && <span className="composer-status-end">{chrome.status}</span>}</div>
    {expandedEditor}
    {notice}
    <label className="sr-only" htmlFor={editor.id}>{t("Message")}</label>
    <PromptEditor {...editor} />
    <ComposerToolbar options={options}>{children}</ComposerToolbar>
  </section>;
}

/**
 * Display names of the current next-Send selection, shown on the collapsed chip. A model without reasoning has none.
 * The key of the provider and the id of the model choose their logos.
 */
export type ComposerSelectionSummary = Readonly<{ agent: string; provider: string; model: string; reasoning: string | null; providerKey?: string | null; modelId?: string | null;
  /** The permission mode the chip shows: one chosen for the session, or the provider's when Claude Code then skips the review. */
  permission?: Readonly<{ id: string; skipsReview: boolean }> | null }>;

// Both draft and owned composers present their selection as one chip that opens this form.
// Supplied controls retain their own catalog/selection authority; `locked` keeps the form
// open while a control owns an admitted change (a provider switch) that must not be orphaned.
export function ComposerSelectionFields({ sessionId, onOpenCatalog, agent, provider, model, reasoning, permission, summary, locked = false }: {
  sessionId: string; onOpenCatalog?: (page: "prompts" | "models") => void;
  agent: ReactNode; provider: ReactNode; model: ReactNode; reasoning: ReactNode;
  /** The permission mode of the session; none for a provider without modes. */
  permission?: ReactNode;
  summary: ComposerSelectionSummary; locked?: boolean;
}) {
  const { t } = useShellLanguage();
  // The logo of the provider, and the one of the model when its family is another brand: a model of Anthropic
  // served by another provider shows both.
  const providerLogo = useProviderBrand(summary.providerKey);
  const modelLogo = modelBrand(summary.modelId, providerLogo);
  const ownModelLogo = modelLogo.icon !== null && modelLogo.icon !== providerLogo.icon;
  const form = <div className="composer-selection-form" role="group" aria-label={t("Session configuration")}>
    <FormGroup label={t("Agent prompt")} labelFor={`composer-agent-${sessionId}`}>{agent}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ProviderIcon providerKey={summary.providerKey} size={14} />{t("Provider")}</span>}>{provider}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ModelIcon modelId={summary.modelId} providerKey={summary.providerKey} size={14} />{t("Model")}</span>} labelFor={`composer-model-${sessionId}`}>{model}</FormGroup>
    <FormGroup label={t("Reasoning")}>{reasoning}</FormGroup>
    {permission && <FormGroup label={t("Permissions")} labelFor={`composer-permission-${sessionId}`}>{permission}</FormGroup>}
    <div className="composer-selection-links">
      <Button variant="minimal" size="small" icon={<AppIcon name="assistant" size={14} />} onClick={() => onOpenCatalog?.("prompts")}>{t("Browse agent prompts")}</Button>
      <Button variant="minimal" size="small" icon={<AppIcon name="model" size={14} />} onClick={() => onOpenCatalog?.("models")}>{t("Browse models")}</Button>
    </div>
  </div>;
  return <PopoverNext content={form} placement="top-start" popoverClassName="composer-selection-popover"
    canEscapeKeyClose={!locked}
    {...(locked ? { isOpen: true, onInteraction: (next: boolean, event?: SyntheticEvent<HTMLElement>) => { if (!next) event?.preventDefault(); } } : {})}>
    <Button variant="minimal" className="composer-selection" aria-label={t("Session configuration")}
      title={t("Agent, model and reasoning for the next Send")} endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part"><AppIcon name="assistant" size={14} /><span>{summary.agent}</span></span>
      <span className="composer-selection-part"><ProviderIcon providerKey={summary.providerKey} size={14} fallback="model" /><span>{summary.provider}</span>
        <span className="composer-selection-separator" aria-hidden="true">/</span>
        {ownModelLogo && <ModelIcon modelId={summary.modelId} providerKey={summary.providerKey} size={14} />}<span>{summary.model}</span></span>
      {summary.reasoning && <span className="composer-selection-part"><AppIcon name="brain" size={14} /><span>{summary.reasoning}</span></span>}
      {summary.permission && <PermissionChipPart id={summary.permission.id} skipsReview={summary.permission.skipsReview} />}
    </Button>
  </PopoverNext>;
}

/** Stepped reasoning-effort slider over the efforts the selected model supports; null when the model is not a listed one. */
export function ReasoningSlider({ value, efforts, disabled = false, onChange }: {
  value: string | null; efforts: readonly string[] | null; disabled?: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const listed = efforts ?? [];
  // A saved effort the selected model does not report stays visible as its own stop.
  const stops = [...listed, ...(value && !listed.includes(value) ? [value] : [])];
  const label = (stop: string) => listed.includes(stop) ? stop : `${stop} · ${t("Unverified")}`;
  // Nothing to choose: a model without reasoning, a single effort, or no listed model yet.
  if (stops.length < 2) return <span className="composer-reasoning-static">{stops.length ? label(stops[0]) : efforts ? t("None") : "—"}</span>;
  const index = Math.max(0, stops.indexOf(value ?? ""));
  return <div className="composer-reasoning-slider" data-stops={stops.length}>
    <Slider min={0} max={stops.length - 1} stepSize={1} labelStepSize={1} value={index} disabled={disabled}
      handleHtmlProps={{ "aria-label": t("Reasoning") }} labelRenderer={stop => label(stops[stop])}
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
    <PopoverNext content={menu} placement="top-end" disabled={optionsDisabled}>
      <Button intent="primary" className="composer-send-options" disabled={optionsDisabled} aria-label={t("Send options")}
        title={t("Send options")} icon={<AppIcon name="chevronDown" size={14} />} />
    </PopoverNext>
  </ButtonGroup>;
}

export function ComposerToolbar({ options, children }: { options?: ReactNode; children: ReactNode }) {
  const { t } = useShellLanguage();
  const chrome = useContext(ComposerChrome);
  return <div className="composer-toolbar">
    {options && <div className="prompt-options" aria-label={t("Session configuration")}>{options}</div>}
    {chrome.context && <div className="composer-context">{chrome.context}</div>}
    <div className="history-controls composer-actions">{children}</div>
  </div>;
}
