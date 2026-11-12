import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { Button, Card } from "@blueprintjs/core";
import { secrets, type SecretPendingEvent } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { raiseAboveWindows } from "../appToaster";
import { useShellLanguage } from "../shellLanguage";
import { confirmationNote, promptedUses, secretsPendingEvent, useKey, whoAsks, withoutAnswer, type PromptText, type PromptedUse } from "./secretPrompt";

type PromptApi = Pick<typeof secrets, "pending" | "confirmUse" | "declineUse">;

/**
 * The prompt of the window for the secrets that ask before use: as soon as a use waits for the user (the shell of a session that
 * starts, a provider, an MCP server), it names the secret and who asks, and offers Confirm…, which the system verifies first
 * (Windows Hello, Touch ID), and Deny. It is shown above the windows of the application, and goes once nothing waits. It reads the
 * uses when it starts and at each `secrets-pending` notice of the shell (`secretsPendingEvent`).
 */
export function SecretUsePrompt({ epoch, api = secrets, sessionTitle }: {
  epoch: string | null; api?: PromptApi;
  /** The title of a session, for the shell of a session that asks; null when it is not known. */
  sessionTitle: (id: string) => string | null;
}) {
  const [event, setEvent] = useState<SecretPendingEvent | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<{ key: string; text: PromptText } | null>(null);
  useEffect(() => {
    setEvent(null);
    if (!epoch) return;
    const abort = new AbortController();
    // The answer of the last read wins: one asked before a later notice is dropped.
    let read = 0;
    const load = () => {
      const mine = ++read;
      void api.pending({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
        .then(value => { if (!abort.signal.aborted && mine === read) setEvent(value.status === "ok" ? value : null); }, () => { /* Read again at the next notice. */ });
    };
    load();
    window.addEventListener(secretsPendingEvent, load);
    // A notice missed while the shell's channel reconnected is caught up a little later.
    const timer = window.setInterval(() => { if (document.visibilityState === "visible") load(); }, 20_000);
    return () => { abort.abort(); window.clearInterval(timer); window.removeEventListener(secretsPendingEvent, load); };
  }, [epoch, api]);
  const uses = promptedUses(event?.pending);
  const current = uses[0] ?? null;
  const layer = usePromptLayer(current !== null);
  if (!current || !layer || !epoch) return null;
  const key = useKey(current);
  async function answer(confirm: boolean) {
    if (!current || !epoch) return;
    setBusy(true);
    try {
      const result = confirm
        ? await api.confirmUse({ expectedEpoch: epoch, name: current.name, use: current.use ?? null }, { timeoutMilliseconds: 180_000 })
        : await api.declineUse({ expectedEpoch: epoch, name: current.name, use: current.use ?? null }, { timeoutMilliseconds: 15_000 });
      const text = confirmationNote(result.status);
      setNote(text ? { key, text } : null);
    } catch {
      setNote({ key, text: { key: "The answer could not be sent.", parameters: {} } });
    } finally {
      setBusy(false);
    }
  }
  return createPortal(<SecretUsePromptCard use={current} more={uses.length - 1} canConfirm={event?.canConfirm ?? false} reason={event?.reason ?? null}
    busy={busy} note={note?.key === key ? note.text : null} sessionTitle={sessionTitle}
    onConfirm={() => void answer(true)} onDeny={() => void answer(false)} />, layer);
}

/** The card of the prompt: one use, the most urgent, and how many more wait. */
export function SecretUsePromptCard({ use, more, canConfirm, reason, busy, note, sessionTitle, onConfirm, onDeny }: {
  use: PromptedUse; more: number; canConfirm: boolean; reason: string | null; busy: boolean; note: PromptText | null;
  sessionTitle: (id: string) => string | null; onConfirm: () => void; onDeny: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const asker = whoAsks(use, sessionTitle);
  const deadline = withoutAnswer(use, locale);
  return <Card className="secret-use-prompt" role="alertdialog" aria-labelledby="secret-use-prompt-title" aria-describedby="secret-use-prompt-asker"
    data-secret={use.name} elevation={3}>
    <h2 id="secret-use-prompt-title"><AppIcon name="shield" size={16} />{t("Use the secret {name}?", { name: use.name })}</h2>
    <p id="secret-use-prompt-asker">{t(asker.key, asker.parameters)}</p>
    {deadline && <p className="secret-use-prompt-deadline">{t(deadline.key, deadline.parameters)}</p>}
    {!canConfirm && <p className="secret-use-prompt-note">{reason ?? t("The system cannot verify you now.")}</p>}
    {note && <p className="secret-use-prompt-note" role="status">{t(note.key, note.parameters)}</p>}
    <div className="secret-use-prompt-actions">
      {more > 0 && <span className="secret-use-prompt-more">{t("{count} more waiting", { count: String(more) })}</span>}
      <Button variant="minimal" text={t("Deny")} disabled={busy} onClick={onDeny} />
      <Button intent="primary" icon={<AppIcon name="shield" size={14} />} text={t("Confirm…")} disabled={busy || !canConfirm}
        title={canConfirm ? t("Windows Hello or Touch ID verifies you first.") : reason ?? undefined} onClick={onConfirm} />
    </div>
  </Card>;
}

// The layer of the prompt: a popover in the top layer, inside the window of the application in front (Settings, a dialog), as the
// toasts are, so that it is never behind a window that makes the page inert. It is raised again while it shows, as windows open.
function usePromptLayer(open: boolean): HTMLElement | null {
  const layer = useRef<HTMLElement | null>(null);
  const [shown, setShown] = useState<HTMLElement | null>(null);
  useEffect(() => {
    if (!open) return;
    const element = layer.current ??= Object.assign(document.createElement("div"), { className: "secret-use-prompt-layer" });
    element.popover = "manual";
    const raise = () => { try { raiseAboveWindows(element); } catch { /* A page without popovers shows nothing more. */ } };
    raise();
    setShown(element);
    const timer = window.setInterval(raise, 1000);
    return () => {
      window.clearInterval(timer);
      try { if (element.matches(":popover-open")) element.hidePopover(); } catch { /* Gone with the page. */ }
      element.remove();
      setShown(null);
    };
  }, [open]);
  return open ? shown : null;
}
