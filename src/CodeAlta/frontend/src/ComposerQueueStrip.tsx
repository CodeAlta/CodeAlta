import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { Button, ButtonGroup } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import { clampQueueCount, queuePreview, type ComposerQueueItem, type createComposerQueue } from "./composerQueue";
import { useShellLanguage } from "./shellLanguage";

type Owner = ReturnType<typeof createComposerQueue>;

/**
 * The prompts waiting above the composer, one compact row each: steering for the running turn first, then
 * the queue in the order it will be sent.
 */
export function ComposerQueueStrip({ owner, epoch, sessionId, disabled, running, retry }: {
  owner: Owner; epoch: string; sessionId: string; disabled: boolean;
  /** A turn is running: a queued prompt can be sent to it as steering. */
  running: boolean;
  retry: (item: ComposerQueueItem) => void;
}) {
  useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const { t } = useShellLanguage();
  const items = owner.list(epoch, sessionId);
  const [editing, setEditing] = useState<string | null>(null);
  const edited = items.find(item => item.id === editing && (item.state === "waiting" || item.state === "failed"));
  // A row open in the editor is not sent under the user's hands.
  useEffect(() => {
    if (!edited) return;
    owner.hold(edited, true);
    return () => owner.hold(edited, false);
  }, [owner, edited?.id]);
  if (!items.length) return null;
  const queued = items.filter(item => item.kind === "Queue");
  const sends = queued.reduce((total, item) => total + item.count, 0);
  const steering = items.length - queued.length;
  let position = 0;
  return <section className="composer-queue-strip" aria-label={t("Queued and steering messages")}>
    <header className="composer-queue-header">
      <span className="composer-queue-summary">
        {steering > 0 && <span data-kind="Steer">{steering === 1 ? t("1 steering") : t("{count} steering", { count: steering })}</span>}
        {queued.length > 0 && <span data-kind="Queue">{queued.length === 1 ? t("1 queued") : t("{count} queued", { count: queued.length })}
          {sends > queued.length && <> · {t("{count} sends", { count: sends })}</>}</span>}
      </span>
      {queued.some(item => item.state === "waiting" || item.state === "failed") && <Button size="small" variant="minimal" className="composer-queue-clear" disabled={disabled}
        title={`${t("Remove every queued prompt")} (F10)`} onClick={() => owner.clear(epoch, sessionId)}>{t("Clear queue")}</Button>}
    </header>
    <ol className="composer-queue-list">
      {items.map(item => <QueueRow key={item.id} item={item} owner={owner} disabled={disabled} running={running} retry={retry}
        position={item.kind === "Queue" ? ++position : 0} edit={() => setEditing(item.id)} />)}
    </ol>
    {edited && !disabled && <ExpandedPromptEditor text={edited.text} onChange={text => owner.edit(edited, text)} onClose={() => setEditing(null)} />}
  </section>;
}

function QueueRow({ item, owner, disabled, running, retry, position, edit }: {
  item: ComposerQueueItem; owner: Owner; disabled: boolean; running: boolean; position: number;
  retry: (item: ComposerQueueItem) => void; edit: () => void;
}) {
  const { t } = useShellLanguage();
  const steer = item.kind === "Steer";
  const editable = !disabled && !steer && (item.state === "waiting" || item.state === "failed");
  const [copied, setCopied] = useState(false);
  const reset = useRef<number>(undefined);
  useEffect(() => () => window.clearTimeout(reset.current), []);
  const copy = () => void navigator.clipboard?.writeText(item.text).then(() => {
    setCopied(true); window.clearTimeout(reset.current);
    reset.current = window.setTimeout(() => setCopied(false), 1500);
  }, () => { /* The clipboard is unavailable: nothing was copied and nothing changes. */ });
  const preview = queuePreview(item.text);
  const images = item.images?.length ?? 0;
  const status = item.state === "sending" ? t("Sending…")
    : item.state === "uncertain" ? t("Not confirmed")
    : item.state === "failed" ? t("Not sent")
    : steer ? t(item.state === "delivering" ? "Steer pending" : "Steering…")
    : null;
  return <li className="composer-queue-row" data-kind={item.kind} data-state={item.state}>
    <span className="composer-queue-mark" aria-hidden="true">
      {steer ? <AppIcon name="steer" size={14} /> : <span className="composer-queue-position">{position}</span>}
    </span>
    <span className="sr-only">{t(steer ? "Steer" : "Queue")}</span>
    <span className="composer-queue-preview" title={item.text}>{preview || t("Prompt")}</span>
    {images > 0 && <span className="composer-queue-images"><AppIcon name="fileImage" size={13} />{images}</span>}
    {status && <span className="composer-queue-status" title={item.code && item.state !== "delivering" ? item.code : undefined}>
      {(item.state === "sending" || steer && item.state !== "uncertain" && item.state !== "failed") && <ActivitySpinner size={12} />}{status}</span>}
    <span className="composer-queue-actions">
      {!steer && <RepeatCount value={item.count} disabled={!editable} onChange={count => owner.edit(item, item.text, count)} />}
      <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
        aria-label={t("Copy prompt")} title={t(copied ? "Copied" : "Copy prompt")} onClick={copy} />
      {!steer && <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} aria-label={t("Edit queued prompt")}
        title={t("Edit queued prompt")} disabled={!editable} onClick={edit} />}
      {!steer && <Button size="small" variant="minimal" icon={<AppIcon name="steer" size={14} />} aria-label={t("Steer now")}
        title={t(images > 0 ? "Prompts with images wait for the next turn; steering accepts text only"
          : running ? "Send now to the running turn as steering" : "Steering needs a running turn")}
        disabled={!editable || item.state !== "waiting" || !running || images > 0 || !item.text.trim()} onClick={() => owner.steerNow(item)} />}
      {(item.state === "uncertain" || item.state === "failed") && <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />}
        aria-label={t("Try again")} title={t("Try again")} disabled={disabled} onClick={() => retry(item)} />}
      <Button size="small" variant="minimal" className="composer-queue-delete" icon={<AppIcon name="trash" size={14} />}
        aria-label={t(steer ? "Delete steering prompt" : "Delete queued prompt")}
        title={t(steer ? item.state === "delivering" ? "Remove from this list (the agent may already have it)" : "Delete steering prompt" : "Delete queued prompt")}
        disabled={disabled || item.state === "sending" || item.state === "uncertain"} onClick={() => owner.remove(item)} />
    </span>
  </li>;
}

/** How many times a queued prompt is sent: a small stepper whose field also takes a typed number. */
function RepeatCount({ value, disabled, onChange }: { value: number; disabled: boolean; onChange: (value: number) => void }) {
  const { t } = useShellLanguage();
  const [typed, setTyped] = useState<string | null>(null);
  const abandoned = useRef(false);
  const commit = () => {
    if (!abandoned.current && typed !== null && /^\d+$/u.test(typed.trim())) { const next = clampQueueCount(Number(typed)); if (next !== value) onChange(next); }
    abandoned.current = false;
    setTyped(null);
  };
  return <ButtonGroup className="composer-queue-count" data-repeats={value > 1}>
    <Button size="small" variant="minimal" icon={<AppIcon name="minus" size={12} />} aria-label={t("Decrease repeat count")} title={t("Decrease repeat count")}
      disabled={disabled || value <= 1} onClick={() => onChange(clampQueueCount(value - 1))} />
    <input inputMode="numeric" aria-label={t("Repeat count")} title={t("Times this prompt is sent")} value={typed ?? `×${value}`} disabled={disabled}
      onFocus={event => { setTyped(String(value)); const field = event.currentTarget; requestAnimationFrame(() => field.select()); }}
      onChange={event => setTyped(event.target.value)} onBlur={commit}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.key === "Enter") { event.preventDefault(); event.currentTarget.blur(); }
        else if (event.key === "Escape") { event.preventDefault(); abandoned.current = true; event.currentTarget.blur(); }
        else if (event.key === "ArrowUp") { event.preventDefault(); onChange(clampQueueCount(value + 1)); setTyped(String(clampQueueCount(value + 1))); }
        else if (event.key === "ArrowDown") { event.preventDefault(); onChange(clampQueueCount(value - 1)); setTyped(String(clampQueueCount(value - 1))); }
      }} />
    <Button size="small" variant="minimal" icon={<AppIcon name="plus" size={12} />} aria-label={t("Increase repeat count")} title={t("Increase repeat count")}
      disabled={disabled || value >= 999} onClick={() => onChange(clampQueueCount(value + 1))} />
  </ButtonGroup>;
}
