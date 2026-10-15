import { useContext, useEffect, useState, type ComponentProps, type ReactNode, type SyntheticEvent } from "react";
import { Button, ButtonGroup, FormGroup, Menu, MenuItem, PopoverNext, Slider } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { ModelIcon, ProviderIcon } from "./ProviderIcon";
import { ComposerChrome } from "./composerChrome";
import { PromptEditor } from "./PromptEditor";
import { permissionModeTone } from "./permissionModes";
import { MoreMenu } from "./promptSurface/MoreMenu";
import { SurfaceDensityContext, useMeasuredDensity } from "./promptSurface/PromptSurfaceParts";
import { useShellLanguage } from "./shellLanguage";

/** The controls used less often, in the `⋯` menu, and what among them needs the user. */
export type ComposerMore = Readonly<{ items: ReactNode; attention?: ComponentProps<typeof MoreMenu>["attention"]; label?: string;
  /** A question one of the controls asks (the archive of a session with child sessions), shown above the `⋯` button. */
  question?: ReactNode }>;

// Shared presentation for owned, new-session and catalog composers. Authority,
// request recovery and draft ownership stay with each composer, not this surface.
// Above the editor: the run status and the background tasks, then where the session works and the plugin status
// (the MCP servers). Below: the Agent chip, the meters (sub-agents, context, prompt cache), the `⋯` menu and the
// actions. The surface measures its own width (a split pane is narrower than the window): labels shorten, then
// become icons, and at last the meters fold into the `⋯` menu, in that order.
export function ComposerSurface({ status, busy = false, children, className, editor, expandedEditor, notice, options, subAgents, meters, more }: {
  status: ReactNode; busy?: boolean; children: ReactNode; className?: string;
  editor: ComponentProps<typeof PromptEditor>; expandedEditor?: ReactNode; notice?: ReactNode; options?: ReactNode;
  /** The sub-agents at work, right of the Agent chip; folded into the `⋯` menu where there is no room. */
  subAgents?: ReactNode;
  /** The context and the prompt cache, right of the Agent chip. */
  meters?: ReactNode;
  more?: ComposerMore;
}) {
  const { t } = useShellLanguage();
  const chrome = useContext(ComposerChrome);
  const [element, setElement] = useState<HTMLElement | null>(null);
  const density = useMeasuredDensity(element);
  return <SurfaceDensityContext.Provider value={density}><section ref={setElement} className={`owned-session prompt-surface${className ? ` ${className}` : ""}`}
    data-density={density} aria-label={t("Message composer")}>
    {chrome.footer}
    <div className="prompt-surface-top">
      <div className="composer-status-line" role="status" data-busy={busy}>{status}</div>
      {(chrome.context || chrome.status) && <div className="prompt-surface-place">{chrome.context}{chrome.status && <span className="composer-status-end">{chrome.status}</span>}</div>}
    </div>
    {expandedEditor}
    {notice}
    <label className="sr-only" htmlFor={editor.id}>{t("Message")}</label>
    <PromptEditor {...editor} />
    <ComposerToolbar options={options} subAgents={subAgents ?? chrome.subAgents} meters={meters} more={more} folded={density === "tiny"}>{children}</ComposerToolbar>
  </section></SurfaceDensityContext.Provider>;
}

/**
 * Display names of the current next-Send selection, shown on the collapsed chip. A model without reasoning has none.
 * The key of the provider and the id of the model choose their logos. `permission` is the mode the session runs in,
 * shown as a shield; null where the host offers no modes.
 */
export type ComposerSelectionSummary = Readonly<{ agent: string; provider: string; model: string; reasoning: string | null; providerKey?: string | null; modelId?: string | null;
  permission?: Readonly<{ mode: string | null; name: string }> | null;
  /** What the tooltip says of the reasoning when the chip shows none ("Haiku 4.5 has no reasoning effort"). */
  reasoningNote?: string | null }>;

// Both draft and owned composers present their selection as one chip, the Agent chip, that opens this form.
// Supplied controls retain their own catalog/selection authority; `locked` keeps the form
// open while a control owns an admitted change (a provider switch) that must not be orphaned.
export function ComposerSelectionFields({ sessionId, onOpenCatalog, agent, provider, model, reasoning, permission, summary, locked = false, openRequest = 0, notice = null }: {
  sessionId: string; onOpenCatalog?: (page: "prompts" | "models") => void;
  agent: ReactNode; provider: ReactNode; model: ReactNode; reasoning: ReactNode;
  /** The permission mode of the session, where the host offers modes. */
  permission?: ReactNode;
  summary: ComposerSelectionSummary; locked?: boolean;
  /** Opens the form each time it changes from what it was: the owner asks for another choice. */
  openRequest?: number;
  /** Why the provider must change, under the provider: the open form covers the status line that says it too. */
  notice?: string | null;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (openRequest > 0) setOpen(true); }, [openRequest]);
  const form = <div className="composer-selection-form" role="group" aria-label={t("Session configuration")}>
    <FormGroup label={t("Agent prompt")} labelFor={`composer-agent-${sessionId}`}>{agent}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ProviderIcon providerKey={summary.providerKey} size={14} />{t("Provider")}</span>}
      intent={notice ? "danger" : "none"} helperText={notice ? <span className="composer-selection-notice" role="alert">{notice}</span> : undefined}>{provider}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ModelIcon modelId={summary.modelId} providerKey={summary.providerKey} size={14} />{t("Model")}</span>} labelFor={`composer-model-${sessionId}`}>{model}</FormGroup>
    <FormGroup label={t("Reasoning")}>{reasoning}</FormGroup>
    {permission && <FormGroup label={<span className="composer-selection-label"><AppIcon name="shield" size={14} />{t("Permissions")}</span>}
      labelFor={`composer-permission-${sessionId}`}>{permission}</FormGroup>}
    <div className="composer-selection-links">
      <Button variant="minimal" size="small" icon={<AppIcon name="assistant" size={14} />} onClick={() => onOpenCatalog?.("prompts")}>{t("Browse agent prompts")}</Button>
      <Button variant="minimal" size="small" icon={<AppIcon name="model" size={14} />} onClick={() => onOpenCatalog?.("models")}>{t("Browse models")}</Button>
    </div>
  </div>;
  // A window too short for the form above or below the chip keeps it whole in the page, over the chip.
  return <PopoverNext content={form} placement="top-start" popoverClassName="composer-selection-popover" middleware={{ shift: { crossAxis: true, padding: 8 } }}
    canEscapeKeyClose={!locked} isOpen={locked || open}
    onInteraction={(next: boolean, event?: SyntheticEvent<HTMLElement>) => { if (locked && !next) { event?.preventDefault(); return; } setOpen(next); }}>
    <Button variant="minimal" className="composer-selection" aria-label={t("Session configuration")}
      title={[`${t("Agent prompt")}: ${summary.agent}`, `${t("Provider")}: ${summary.provider}`, `${t("Model")}: ${summary.model}`,
        summary.reasoning ? `${t("Reasoning")}: ${summary.reasoning}` : summary.reasoningNote,
        summary.permission && `${t("Permissions")}: ${summary.permission.name}`].filter(Boolean).join("\n")}
      endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part composer-selection-agent"><AppIcon name="assistant" size={14} /><span>{summary.agent}</span></span>
      {/* One logo: the model's, else the one of its provider. The provider is named in the tooltip. */}
      <span className="composer-selection-part composer-selection-model"><ModelIcon modelId={summary.modelId} providerKey={summary.providerKey} size={14} />
        <span className="composer-selection-model-name">{summary.model}</span></span>
      {summary.reasoning && <span className="composer-selection-part composer-selection-reasoning"><AppIcon name="brain" size={14} /><span>{summary.reasoning}</span></span>}
      {summary.permission && <span className="composer-selection-part composer-selection-permission" data-tone={permissionModeTone(summary.permission.mode)}>
        <AppIcon name="shield" size={14} /><span className="composer-selection-permission-name">{summary.permission.name}</span></span>}
    </Button>
  </PopoverNext>;
}

/**
 * Stepped reasoning-effort slider over the efforts the selected model supports; `efforts` is null when the model is not a
 * listed one. `complete` says that the provider sends no other effort: a model that lists none has no reasoning, and a
 * saved effort it does not list is not used. Otherwise such an effort may still be sent, and is only unverified.
 */
export function ReasoningSlider({ value, efforts, complete = false, model = null, disabled = false, onChange }: {
  value: string | null; efforts: readonly string[] | null; complete?: boolean;
  /** The name of the model, for what is said of a model without reasoning. */
  model?: string | null;
  disabled?: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const listed = efforts ?? [];
  if (complete && efforts && listed.length === 0)
    return <span className="composer-reasoning-static" data-none="true">{model ? t("{model} has no reasoning effort", { model }) : t("None")}</span>;
  // A saved effort the selected model does not report stays visible as its own stop.
  const stops = [...listed, ...(value && !listed.includes(value) ? [value] : [])];
  const label = (stop: string) => listed.includes(stop) ? stop : `${stop} · ${t(complete ? "Not used" : "Unverified")}`;
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
export function SendSplitButton({ enqueue, onEnqueueChange, busy = false, enqueueDisabled = false, optionsDisabled = false, children }: {
  enqueue: boolean; onEnqueueChange: (value: boolean) => void; busy?: boolean; enqueueDisabled?: boolean; optionsDisabled?: boolean; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const menu = <Menu aria-label={t("Send options")}>
    <MenuItem roleStructure="listoption" selected={!enqueue} icon={<AppIcon name={busy ? "steer" : "send"} size={16} />} text={t(busy ? "Send now (steer)" : "Send now")}
      label={navigator.platform.startsWith("Mac") ? "Cmd+Enter" : "Ctrl+Enter"} title={t("Sent immediately.")} onClick={() => onEnqueueChange(false)} />
    <MenuItem roleStructure="listoption" selected={enqueue} disabled={enqueueDisabled} icon={<AppIcon name="queue" size={16} />}
      text={t("Enqueue until idle")} label="Enter"
      title={t("Sent as soon as the session is idle.")} onClick={() => onEnqueueChange(true)} />
  </Menu>;
  return <ButtonGroup className="composer-send-group">
    {children}
    <PopoverNext content={menu} placement="top-end" disabled={optionsDisabled}>
      <Button intent="primary" className="composer-send-options" disabled={optionsDisabled} aria-label={t("Send options")}
        title={t("Send options")} icon={<AppIcon name="chevronDown" size={14} />} />
    </PopoverNext>
  </ButtonGroup>;
}

export function ComposerToolbar({ options, subAgents, meters, more, folded = false, children }: {
  options?: ReactNode;
  /** The sub-agents at work: first of the meters, folded into the `⋯` menu where there is no room. */
  subAgents?: ReactNode;
  /** The context and the prompt cache: always shown. */
  meters?: ReactNode;
  more?: ComposerMore;
  /** The sub-agents go into the `⋯` menu: the surface is too narrow for them. */
  folded?: boolean;
  children: ReactNode;
}) {
  const { t } = useShellLanguage();
  // What is folded into the menu keeps its mark on the button.
  const attention = more?.attention ?? (folded && subAgents ? { tone: "info" as const } : null);
  return <div className="composer-toolbar">
    {options && <div className="prompt-options" aria-label={t("Session configuration")}>{options}</div>}
    <div className="prompt-surface-end">
      {(subAgents && !folded || meters) && <div className="prompt-surface-meters">{!folded && subAgents}{meters}</div>}
      {(more || folded && subAgents) && <span className="prompt-surface-more-anchor">
        <MoreMenu attention={attention} label={more?.label}>
          {folded && subAgents && <div className="prompt-surface-folded">{subAgents}</div>}
          {more?.items}
        </MoreMenu>
        {more?.question}
      </span>}
      <div className="history-controls composer-actions">{children}</div>
    </div>
  </div>;
}
