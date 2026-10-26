import { useEffect, useState, type ReactNode, type SyntheticEvent } from "react";
import { Button, FormGroup, HTMLSelect, PopoverNext, Slider, Switch } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { modelBrand } from "./brands";
import { ModelIcon, ProviderIcon } from "./ProviderIcon";
import { permissionModeTone } from "./permissionModes";
import { sandboxSources, type SessionSandboxSummary } from "./sandbox/sandboxChoices";
import { useShellLanguage } from "./shellLanguage";

// The Agent chip of a composer and its form: the agent prompt, the provider, the model, the reasoning, the
// permission mode, the review and the sandbox of a session. It loads no editor.

/**
 * Display names of the current next-Send selection, shown on the collapsed chip: `provider` is the name Settings gives
 * the provider ("Claude Business"), not its key. A model without reasoning has none.
 * The key of the provider and the id of the model choose their logos. `permission` is the mode the session runs in,
 * shown as a shield; null where the host offers no modes.
 */
export type ComposerSelectionSummary = Readonly<{ agent: string; provider: string; model: string; reasoning: string | null; providerKey?: string | null; modelId?: string | null;
  permission?: Readonly<{ mode: string | null; name: string }> | null;
  /** Whether the user reviews the changes before an agent commits them, shown as an eye; null where it is not known. */
  review?: boolean | null;
  /** The sandbox the session runs in, shown as a small box when it is sandboxed; null where it is not known. */
  sandbox?: SessionSandboxSummary | null;
  /** What the tooltip says of the reasoning when the chip shows none ("Haiku 4.5 has no reasoning effort"). */
  reasoningNote?: string | null }>;

/**
 * The switch at the top of the form of a session that does not exist yet and is started elsewhere than in a composer
 * (an automation, a task, an issue): on, the session takes the defaults, which the disabled rows show.
 */
export type ComposerSelectionDefaults = Readonly<{ on: boolean; disabled?: boolean; onChange: (on: boolean) => void }>;

// Both draft and owned composers present their selection as one chip, the Agent chip, that opens this form; so do the
// places that start a session of their own (an automation, a task, an issue), with the switch of the defaults.
// Supplied controls retain their own catalog/selection authority; `locked` keeps the form
// open while a control owns an admitted change (a provider switch) that must not be orphaned.
export function ComposerSelectionFields({ sessionId, onOpenCatalog, agent, provider, model, reasoning, modelOptions, permission, review, sandbox, summary, locked = false, openRequest = 0, notice = null,
  defaults = null, disabled = false }: {
  sessionId: string;
  /** Opens a page of the catalog; the links to it are left out without it. */
  onOpenCatalog?: (page: "prompts" | "models") => void;
  agent: ReactNode; provider: ReactNode; model: ReactNode; reasoning: ReactNode;
  /** The other options of the model (its context, a fast mode), where its provider offers some. */
  modelOptions?: ReactNode;
  /** The permission mode of the session, where the host offers modes. */
  permission?: ReactNode;
  /** Whether the user reviews the changes of the session before an agent commits them. */
  review?: ReactNode;
  /** The sandbox the commands and the file tools of the session run in, where the host has one. */
  sandbox?: ReactNode;
  summary: ComposerSelectionSummary; locked?: boolean;
  /** Opens the form each time it changes from what it was: the owner asks for another choice. */
  openRequest?: number;
  /** Why the provider must change, under the provider: the open form covers the status line that says it too. */
  notice?: string | null;
  /** The switch of the defaults, for a session started elsewhere than in a composer. */
  defaults?: ComposerSelectionDefaults | null;
  /** The chip does not open: what it would change is being used. */
  disabled?: boolean;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  useEffect(() => { if (openRequest > 0) setOpen(true); }, [openRequest]);
  // Whether the model has a logo of its own (the family of its id), apart from the one of its provider.
  const modelLogo = modelBrand(summary.modelId).icon !== null;
  const form = <div className="composer-selection-form" role="group" aria-label={t("Session configuration")}>
    {defaults && <Switch className="composer-selection-defaults" checked={defaults.on} disabled={defaults.disabled} label={t("Use the defaults")}
      onChange={event => defaults.onChange(event.currentTarget.checked)} />}
    <FormGroup label={t("Agent prompt")} labelFor={`composer-agent-${sessionId}`}>{agent}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ProviderIcon providerKey={summary.providerKey} size={14} />{t("Provider")}</span>}
      intent={notice ? "danger" : "none"} helperText={notice ? <span className="composer-selection-notice" role="alert">{notice}</span> : undefined}>{provider}</FormGroup>
    <FormGroup label={<span className="composer-selection-label"><ModelIcon modelId={summary.modelId} providerKey={summary.providerKey} size={14} />{t("Model")}</span>} labelFor={`composer-model-${sessionId}`}>{model}</FormGroup>
    <FormGroup label={t("Reasoning")}>{reasoning}</FormGroup>
    {modelOptions}
    {permission && <FormGroup label={<span className="composer-selection-label"><AppIcon name="shield" size={14} />{t("Permissions")}</span>}
      labelFor={`composer-permission-${sessionId}`}>{permission}</FormGroup>}
    {review && <FormGroup label={<span className="composer-selection-label"><AppIcon name="eye" size={14} />{t("Review before commit")}</span>}
      labelFor={`composer-review-${sessionId}`}>{review}</FormGroup>}
    {sandbox && <FormGroup label={<span className="composer-selection-label"><AppIcon name="sandbox" size={14} />{t("Sandbox")}</span>}
      labelFor={`composer-sandbox-${sessionId}`} intent={summary.sandbox?.problem ? "danger" : "none"}
      helperText={summary.sandbox?.problem ? <span className="composer-selection-notice" role="alert">{summary.sandbox.problem}</span>
        : summary.sandbox ? sandboxSourceText(t, summary.sandbox) : undefined}>{sandbox}</FormGroup>}
    {onOpenCatalog && <div className="composer-selection-links">
      <Button variant="minimal" size="small" icon={<AppIcon name="assistant" size={14} />} onClick={() => onOpenCatalog("prompts")}>{t("Browse agent prompts")}</Button>
      <Button variant="minimal" size="small" icon={<AppIcon name="model" size={14} />} onClick={() => onOpenCatalog("models")}>{t("Browse models")}</Button>
    </div>}
  </div>;
  // A window too short for the form above or below the chip keeps it whole in the page, over the chip.
  return <PopoverNext content={form} placement="top-start" popoverClassName="composer-selection-popover" middleware={{ shift: { crossAxis: true, padding: 8 } }}
    canEscapeKeyClose={!locked} isOpen={!disabled && (locked || open)} disabled={disabled}
    onInteraction={(next: boolean, event?: SyntheticEvent<HTMLElement>) => { if (locked && !next) { event?.preventDefault(); return; } setOpen(next); }}>
    <Button variant="minimal" className="composer-selection" aria-label={t("Session configuration")} disabled={disabled} data-defaults={defaults?.on || undefined}
      title={[defaults?.on && t("Use the defaults"), `${t("Agent prompt")}: ${summary.agent}`, `${t("Provider")}: ${summary.provider}`, `${t("Model")}: ${summary.model}`,
        summary.reasoning ? `${t("Reasoning")}: ${summary.reasoning}` : summary.reasoningNote,
        summary.permission && `${t("Permissions")}: ${summary.permission.name}`,
        summary.review != null && `${t("Review before commit")}: ${t(summary.review ? "On" : "Off")}`,
        summary.sandbox && sandboxTitle(t, summary.sandbox)].filter(Boolean).join("\n")}
      endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part composer-selection-agent"><AppIcon name="assistant" size={14} /><span>{summary.agent}</span></span>
      {/* `<provider logo> Provider / <model logo> Model`: several accounts of one kind of provider (two of Claude Code) are
          told apart by their names. The line is drawn in reverse and wraps: a provider without room goes whole, logo and
          name, to a second line the chip does not show, and the model keeps the room. A model without a logo of its own
          has the provider's, drawn once: outside the line, where it stays when the name of the provider goes. */}
      <span className="composer-selection-part composer-selection-model">
        {!modelLogo && <ProviderIcon providerKey={summary.providerKey} size={14} />}
        <span className="composer-selection-model-line">
          <span className="composer-selection-model-unit">{modelLogo && <ModelIcon modelId={summary.modelId} size={14} />}
            <span className="composer-selection-model-name">{summary.model}</span></span>
          <span className="composer-selection-provider">{modelLogo && <ProviderIcon providerKey={summary.providerKey} size={14} />}
            <span className="composer-selection-provider-name">{summary.provider}</span><span className="composer-selection-slash" aria-hidden="true">/</span></span>
        </span></span>
      {summary.reasoning && <span className="composer-selection-part composer-selection-reasoning"><AppIcon name="brain" size={14} /><span>{summary.reasoning}</span></span>}
      {summary.permission && <span className="composer-selection-part composer-selection-permission" data-tone={permissionModeTone(summary.permission.mode)}>
        <AppIcon name="shield" size={14} /><span className="composer-selection-permission-name">{summary.permission.name}</span></span>}
      {/* The changes wait for the user's review before a commit. */}
      {summary.review && <span className="composer-selection-part composer-selection-review"><AppIcon name="eye" size={14} />
        <span className="composer-selection-review-name">{t("Review before commit")}</span></span>}
      {/* The commands of the session run in a sandbox: a small box, marked when its profile cannot be used. */}
      {summary.sandbox?.profile && <span className="composer-selection-part composer-selection-sandbox" data-problem={summary.sandbox.problem ? "true" : undefined}
        title={sandboxTitle(t, summary.sandbox)}><AppIcon name="sandbox" size={14} /></span>}
    </Button>
  </PopoverNext>;
}

type Translate = ReturnType<typeof useShellLanguage>["t"];
// Where the sandbox of a session comes from.
const sandboxSourceText = (t: Translate, sandbox: SessionSandboxSummary) => sandboxSources[sandbox.source] ? t(sandboxSources[sandbox.source]) : undefined;
// What the chip says of the sandbox: its profile and where it comes from, or why the profile cannot be used.
function sandboxTitle(t: Translate, sandbox: SessionSandboxSummary): string {
  if (!sandbox.profile) return `${t("Sandbox")}: ${t("Off")}`;
  const detail = sandbox.problem ?? sandboxSourceText(t, sandbox);
  return `${t("Sandbox")}: ${sandbox.profile}${detail ? ` · ${detail}` : ""}`;
}

/** An option of a model other than its effort, as the host offers it: its values, the one it has, whether it is on or off. */
export type ComposerModelOption = Readonly<{ id: string; name: string; description?: string | null; value: string;
  values: ReadonlyArray<Readonly<{ value: string; name: string }>>; toggle?: boolean | null }>;

/**
 * The options of the selected model other than its effort, such as its context or a fast mode: a switch for one that
 * is on or off, a list for the others. They are the model's, in every session of its provider.
 */
export function ModelOptionFields({ sessionId, options, disabled = false, onChange }: {
  sessionId: string; options: ReadonlyArray<ComposerModelOption>; disabled?: boolean; onChange: (optionId: string, value: string) => void;
}) {
  const { t } = useShellLanguage();
  return <>{options.map(option => {
    const id = `composer-option-${sessionId}-${option.id}`;
    const title = option.description ?? t("{option} of this model, in every session of its provider", { option: option.name });
    if (option.toggle) {
      const on = option.value.toLowerCase() === "true";
      return <FormGroup key={option.id} className="composer-model-option">
        <Switch id={id} checked={on} disabled={disabled} label={option.name} title={title} onChange={() => onChange(option.id, on ? "false" : "true")} />
      </FormGroup>;
    }
    return <FormGroup key={option.id} label={option.name} labelFor={id} className="composer-model-option">
      <HTMLSelect fill id={id} value={option.value} disabled={disabled} title={title} onChange={event => onChange(option.id, event.target.value)}>
        {option.values.map(value => <option key={value.value} value={value.value}>{value.name}</option>)}
      </HTMLSelect>
    </FormGroup>;
  })}</>;
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
