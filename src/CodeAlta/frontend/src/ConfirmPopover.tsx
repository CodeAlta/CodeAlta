import { Button, Checkbox, PopoverNext, type Intent } from "@blueprintjs/core";
import { useEffect, useId, useRef, useState } from "react";
import { useShellLanguage } from "./shellLanguage";

/**
 * A question about a row of the Explorer, in a popover beside that row, as its rename form is. Rendered inside
 * the row while it is asked; the row must be positioned so the popover's anchor covers it.
 * The button that does it has the focus, so Enter answers yes; Escape, Cancel or a click elsewhere leaves
 * everything as it is and gives the focus back to the row. A check box asks not to be asked the next time.
 */
export function ConfirmPopover({ title, subject, detail, confirmLabel, intent = "primary", onConfirm, onCancel, busy, error = null }: {
  /** The question. */
  title: string;
  /** What it is about: the title of the session, the name of the project. */
  subject: string;
  /** What answering yes does and leaves. */
  detail: string;
  confirmLabel: string;
  intent?: Intent;
  /** The answer is yes; `remember` says the question is not to be asked again. */
  onConfirm: (remember: boolean) => void;
  onCancel: () => void;
  /** It is being done: the question waits for it and cannot be dismissed. */
  busy: boolean;
  /** Why it could not be done. */
  error?: string | null;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const anchor = useRef<HTMLSpanElement>(null);
  const confirm = useRef<HTMLButtonElement>(null);
  const [remember, setRemember] = useState(false);
  useEffect(() => { const timer = setTimeout(() => confirm.current?.focus()); return () => clearTimeout(timer); }, []);
  function cancel() {
    const row = anchor.current?.closest(".session-row, .project-action-row")?.querySelector<HTMLElement>(":scope > button");
    onCancel();
    row?.focus();
  }
  const question = <div className="confirm-form" role="alertdialog" aria-labelledby={`${id}-title`} aria-describedby={`${id}-detail`}
    onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); cancel(); } }}>
    <strong id={`${id}-title`}>{title}</strong>
    <span className="confirm-subject" title={subject}>{subject}</span>
    <p id={`${id}-detail`}>{detail}</p>
    {error && <p role="alert" className="confirm-error">{error}</p>}
    <Checkbox checked={remember} disabled={busy} label={t("Do not ask again")} onChange={event => setRemember(event.currentTarget.checked)} />
    <div className="rename-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={cancel} />
      <Button ref={confirm} intent={intent} size="small" text={confirmLabel} loading={busy} onClick={() => onConfirm(remember)} />
    </div>
  </div>;
  return <PopoverNext isOpen content={question} placement="right-start" popoverClassName="rename-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) cancel(); }}>
    <span ref={anchor} aria-hidden="true" />
  </PopoverNext>;
}
