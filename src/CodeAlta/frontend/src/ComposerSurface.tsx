import { useContext, useState, type ComponentProps, type DragEvent, type ReactNode } from "react";
import { Button, ButtonGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { ComposerChrome } from "./composerChrome";
import { PromptEditor } from "./PromptEditor";
import { MoreMenu } from "./promptSurface/MoreMenu";
import { SurfaceDensityContext, useMeasuredDensity } from "./promptSurface/PromptSurfaceParts";
import { useShellLanguage } from "./shellLanguage";

// The Agent chip and its form live apart from the editor, so that the places that start a session of their own
// (an automation, a task, an issue) use them without loading the editor.
export { ComposerSelectionFields, ModelOptionFields, ReasoningSlider, type ComposerModelOption, type ComposerSelectionDefaults,
  type ComposerSelectionSummary } from "./ComposerSelection";

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
export function ComposerSurface({ status, busy = false, children, className, editor, expandedEditor, notice, options, subAgents, meters, more, onDropFiles }: {
  status: ReactNode; busy?: boolean; children: ReactNode; className?: string;
  /** Takes the files dropped on the composer, from the file manager of the system; null where nothing can be attached. */
  onDropFiles?: ((files: File[], origin: HTMLElement) => void) | null;
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
  // Only files from outside the page: a drag inside the page (a tab, a file of the Explorer) carries no file. They are
  // taken before the editor sees them (capture), or it would also insert their paths (`text/uri-list`) as text.
  const dropping = (event: DragEvent<HTMLElement>, value: boolean) => { if (value) event.currentTarget.dataset.dropping = "true"; else delete event.currentTarget.dataset.dropping; };
  return <SurfaceDensityContext.Provider value={density}><section ref={setElement} className={`owned-session prompt-surface${className ? ` ${className}` : ""}`}
    data-density={density} aria-label={t("Message composer")}
    onDragOverCapture={onDropFiles ? event => {
      if (!event.dataTransfer.types.includes("Files")) return;
      event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = "copy"; dropping(event, true);
    } : undefined}
    onDragLeave={onDropFiles ? event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) dropping(event, false); } : undefined}
    onDropCapture={onDropFiles ? event => {
      dropping(event, false);
      if (!event.dataTransfer.files.length) return;
      event.preventDefault(); event.stopPropagation();
      onDropFiles(Array.from(event.dataTransfer.files), event.currentTarget);
    } : undefined}>
    {chrome.footer}
    <div className="prompt-surface-top">
      <div className="composer-status-line" role="status" data-busy={busy}>{status}</div>
      {(chrome.context || chrome.status) && <div className="prompt-surface-place">{chrome.context}{chrome.status && <span className="composer-status-end">{chrome.status}</span>}</div>}
    </div>
    {expandedEditor}
    {notice}
    <label className="sr-only" htmlFor={editor.id}>{t("Message")}</label>
    {chrome.promptCover}
    <PromptEditor {...editor} covered={!!chrome.promptCover} />
    <ComposerToolbar options={options} subAgents={subAgents ?? chrome.subAgents} meters={meters} more={more} folded={density === "tiny"}>{children}</ComposerToolbar>
  </section></SurfaceDensityContext.Provider>;
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
