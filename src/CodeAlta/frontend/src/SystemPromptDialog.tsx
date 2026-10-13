import { createContext, useContext, useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ComponentType } from "react";
import { SegmentedControl, Tab, Tabs, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { isDialogBackdrop } from "./dialogBackdrop";
import { MarkdownContent } from "./MarkdownContent";
import { timelineTime } from "./sessionTime";
import { useShellLanguage } from "./shellLanguage";
import { changedParts, promptDocument, promptProblem, promptTokens, shortHash, type PromptRow } from "./systemPrompt";
import type { PromptDiffLook, PromptDiffProps } from "./SystemPromptDiff";
import type { SystemPromptRead, SystemPromptReader } from "./systemPromptReader";
import type { TimelineItem } from "./timeline";
import { CopyButton } from "./ToolCallChrome";

/** Reads the system prompts of the session a timeline shows; null where nothing reads them (a detached fixture). */
export const SystemPromptReaderContext = createContext<SystemPromptReader | null>(null);
/**
 * The view that compares two prompts, which brings the diff editor with it. The application gives it; null
 * where the editor is not loaded (a fixture of the timeline): a prompt is then shown without its comparison.
 */
export const SystemPromptDiffContext = createContext<ComponentType<PromptDiffProps> | null>(null);

type Look = PromptDiffLook & Readonly<{ source: boolean }>;
const lookKey = "codealta.desktop.system-prompt.look.v1";
const defaultLook: Look = { sideBySide: true, wrap: true, collapseUnchanged: true, source: false };

// How the user last looked at a prompt: kept for the next window.
function restoreLook(): Look {
  try {
    const raw = localStorage.getItem(lookKey);
    const value = raw && raw.length <= 512 ? JSON.parse(raw) as Record<string, unknown> | null : null;
    if (!value || typeof value !== "object") return defaultLook;
    const flag = (name: keyof Look) => typeof value[name] === "boolean" ? value[name] as boolean : defaultLook[name];
    return { sideBySide: flag("sideBySide"), wrap: flag("wrap"), collapseUnchanged: flag("collapseUnchanged"), source: flag("source") };
  } catch { return defaultLook; }
}

const unread: SystemPromptRead = Object.freeze({ status: "failed" });

// The whole record of a prompt: what the page kept of it, else what the host reads.
function usePromptRecord(reader: SystemPromptReader | null, offset: string | null): SystemPromptRead | undefined {
  const [loaded, setLoaded] = useState<{ reader: SystemPromptReader; offset: string; read: SystemPromptRead } | null>(null);
  useEffect(() => {
    if (!reader || !offset) return;
    const known = reader.peek(offset);
    if (known) { setLoaded({ reader, offset, read: known }); return; }
    let active = true;
    void reader.read(offset).then(read => { if (active) setLoaded({ reader, offset, read }); },
      () => { if (active) setLoaded({ reader, offset, read: unread }); });
    return () => { active = false; };
  }, [reader, offset]);
  if (!reader || !offset) return unread;
  return loaded && loaded.reader === reader && loaded.offset === offset ? loaded.read : reader.peek(offset);
}

function Loading() {
  const { t } = useShellLanguage();
  return <div className="prompt-loading" role="status"><ActivitySpinner size={16} /><span>{t("Loading…")}</span></div>;
}

/**
 * A system prompt of a session, in a window: what it is, the prompt as it was sent and, for a prompt that
 * replaced another, what changed. The header is drawn at once from the row; the texts are read from the host.
 */
export function SystemPromptDialog({ item, current, onClose }: { item: TimelineItem; current: () => boolean; onClose: () => void }) {
  const { t, locale } = useShellLanguage();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  const reader = useContext(SystemPromptReaderContext);
  const PromptDiff = useContext(SystemPromptDiffContext);
  const [chosen, setChosen] = useState<"changes" | "prompt" | null>(null);
  const [look, setLook] = useState(restoreLook);
  const update = (change: Partial<Look>) => setLook(value => {
    const next = { ...value, ...change };
    try { localStorage.setItem(lookKey, JSON.stringify(next)); } catch { /* The choice lasts as long as the window. */ }
    return next;
  });
  useLayoutEffect(() => {
    const element = dialog.current!;
    if (!current()) { onClose(); return; }
    element.showModal(); closeButton.current?.focus();
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== element) onClose(); };
    document.addEventListener("beforetoggle", retire, true);
    return () => { document.removeEventListener("beforetoggle", retire, true); if (element.open) element.close(); };
  }, []);
  useLayoutEffect(() => { if (!current()) onClose(); });

  const row: PromptRow = item.prompt!;
  const offset = /^\d+$/u.test(item.key) ? item.key : null;
  const read = usePromptRecord(reader, offset);
  const record = read?.status === "ready" ? read.record : null;
  const pending = read === undefined;
  // The record says the same as the row, and more: the row stands for it while it is read.
  const facts = record ?? row;
  const previous = record?.previous ?? null;
  const change = record?.change ?? row.change ?? (reader && offset ? reader.peekChange(offset) ?? null : null);
  // A change is compared once the prompt it replaced is there; a first prompt has nothing to compare with.
  const comparable = !!PromptDiff && (previous !== null || pending && row.changed);
  const view = comparable && chosen !== "prompt" ? "changes" : "prompt";
  const problem = promptProblem(facts);
  const time = timelineTime(record?.timestamp || item.timestamp, locale);
  const titles = useMemo(() => ({ system: t("System message"), developer: t("Developer instructions") }), [locale]);
  const whole = useMemo(() => record ? promptDocument(record.system?.text ?? null, record.developer?.text ?? null, titles) : "", [record, titles]);
  const before = useMemo(() => previous ? promptDocument(previous.system?.text ?? null, previous.developer?.text ?? null, titles) : "", [previous, titles]);
  const parts = useMemo(() => record && previous ? changedParts({ system: previous.system?.text ?? null, developer: previous.developer?.text ?? null },
    { system: record.system?.text ?? null, developer: record.developer?.text ?? null }) : null, [record, previous]);
  const partial = !!record && [record.system, record.developer, previous?.system, previous?.developer].some(text => text?.partial);

  const failure = <p className="tool-empty prompt-failed" role="alert">{t("The prompt could not be read.")}</p>;
  const promptView = <div className="tool-panel prompt-view" data-view="prompt">
    {pending ? <Loading /> : !record ? failure : <>
      <div className="prompt-toolbar">
        <SegmentedControl size="small" aria-label={t("View")} value={look.source ? "source" : "read"} onValueChange={value => update({ source: value === "source" })}
          options={[{ value: "read", label: t("Read") }, { value: "source", label: t("Source") }]} />
        <span className="changes-spacer" />
        <CopyButton text={whole} label={t("Copy the prompt as Markdown")} />
      </div>
      <div className="prompt-parts" tabIndex={0} role="group" aria-label={t("Prompt")}>
        {([["system", record.system, record.systemTokens], ["developer", record.developer, record.developerTokens]] as const).map(([name, text, tokens]) =>
          <section className="prompt-part" key={name} data-part={name}>
            <header><h3>{titles[name]}</h3>{tokens > 0 && <small>{t("{count} tokens", { count: promptTokens(tokens) })}</small>}</header>
            {!text?.text.trim() ? <p className="tool-empty">{t("Empty")}</p>
              : look.source ? <pre className="prompt-source-text">{text.text.trim()}</pre>
                : <MarkdownContent source={text.text.trim()} document />}
            {text?.partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
          </section>)}
      </div>
    </>}
  </div>;
  const changesView = <div className="tool-panel prompt-view" data-view="changes">
    {!record || !previous || !PromptDiff ? <Loading /> : <>
      <PromptDiff documentKey={`${offset}`} original={before} modified={whole} look={look} onLook={update}
        summary={<span className="prompt-diff-summary">
          {change && <span className="file-counts" title={t("Lines added and removed")}><b>+{change.added}</b> <em>−{change.removed}</em></span>}
          {parts && (parts.system || parts.developer) && <span className="prompt-diff-parts">
            {[parts.system && titles.system, parts.developer && titles.developer].filter(Boolean).join(" · ")}</span>}
        </span>} />
      {partial && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
    </>}
  </div>;

  return <dialog ref={dialog} className="app-dialog system-prompt-dialog" aria-labelledby={id} data-prompt-state={facts.changed ? "changed" : "recorded"} data-view={view}
    onClick={event => { if (!composing.current && isDialogBackdrop(event)) onClose(); }}
    // Effect replay can reopen the element before cleanup's queued close event arrives.
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose(); } }}>
    <AppWindowSurface storageKey="codealta.desktop.window.system-prompt.v1" titleId={id} minimumSize={{ width: 420, height: 300 }}
      title={<span className="tool-title"><AppIcon name="prompt" size={15} /><span className="prompt-title-name">{t("System prompt")}</span>
        <Tag minimal round className="tool-state" intent={facts.changed ? "primary" : "none"}>{t(facts.changed ? "Changed" : "Recorded")}</Tag></span>}
      preferredSize={viewport => ({ width: Math.min(1120, viewport.width - 40), height: Math.min(780, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={closeButton}>
      <div className="tool-summary prompt-facts">
        {facts.name && <span className="tool-fact prompt-agent" title={t("Agent prompt")}><strong>{facts.name}</strong></span>}
        {facts.sourcePath && <span className="tool-fact tool-fact-path prompt-source" title={facts.sourcePath}><bdi>{facts.sourcePath}</bdi>
          <CopyButton text={facts.sourcePath} label={t("Copy path")} /></span>}
        {facts.tokens > 0 && <span className="tool-fact prompt-size"><AppIcon name="usage" size={13} />{t("{count} tokens", { count: promptTokens(facts.tokens) })}
          <small>{t("{system} system · {developer} developer", { system: promptTokens(facts.systemTokens), developer: promptTokens(facts.developerTokens) })}</small></span>}
        <span className="tool-fact" title={time.title}><AppIcon name="reminder" size={13} /><time dateTime={time.dateTime}>{time.label}</time></span>
        {facts.hash && <span className="tool-fact prompt-hash" title={facts.hash}><code>{shortHash(facts.hash)}</code><CopyButton text={facts.hash} label={t("Copy hash")} /></span>}
        {problem ? <Tag minimal intent="warning" className="prompt-problem" icon={<AppIcon name="warning" size={12} />}
          title={t(problem === "lossy" ? "The provider could not take this prompt as it is written." : "This prompt was not sent to the provider.")}>
          {t(problem === "lossy" ? "Adapted for the provider" : "Not sent to the provider")}</Tag>
          : <span className="tool-fact prompt-delivery"><AppIcon name="check" size={13} />{t("Sent as written")}</span>}
      </div>
      {comparable ? <Tabs id={`${id}-tabs`} className="tool-tabs prompt-tabs" selectedTabId={view} onChange={value => setChosen(value === "prompt" ? "prompt" : "changes")} renderActiveTabPanelOnly>
        <Tab id="changes" disabled={false} title={t("Changes")} panel={changesView} />
        <Tab id="prompt" disabled={false} title={t("Prompt")} panel={promptView} />
      </Tabs> : promptView}
    </AppWindowSurface>
  </dialog>;
}
