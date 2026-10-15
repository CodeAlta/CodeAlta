import { Button, Checkbox, PopoverNext, type Intent } from "@blueprintjs/core";
import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { useShellLanguage } from "./shellLanguage";

/**
 * A question about a row of the Explorer, in a popover beside that row, as its rename form is. Rendered inside
 * the row while it is asked; the row must be positioned so the popover's anchor covers it.
 * The button that does it has the focus, so Enter answers yes; Escape, Cancel or a click elsewhere leaves
 * everything as it is and gives the focus back to the row. A check box asks not to be asked the next time.
 * A question that is always asked has no check box, and Cancel has the focus: Enter leaves everything as it is.
 */
export function ConfirmPopover({ title, subject, detail, confirmLabel, intent = "primary", always = false, onConfirm, onCancel, busy, error = null, children }: {
  /** The question. */
  title: string;
  /** What it is about: the title of the session, the name of the project. */
  subject: string;
  /** What answering yes does and leaves. */
  detail: string;
  confirmLabel: string;
  intent?: Intent;
  /** The question is asked every time: what it does is too much to do without being asked. */
  always?: boolean;
  /** The answer is yes; `remember` says the question is not to be asked again. */
  onConfirm: (remember: boolean) => void;
  onCancel: () => void;
  /** It is being done: the question waits for it and cannot be dismissed. */
  busy: boolean;
  /** Why it could not be done. */
  error?: string | null;
  /** What the answer chooses besides yes, as an archive with or without the child sessions; the button that confirms
   * takes the focus, as for a question that can be told not to be asked again. */
  children?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const anchor = useRef<HTMLSpanElement>(null);
  const confirm = useRef<HTMLButtonElement>(null);
  const dismiss = useRef<HTMLButtonElement>(null);
  const [remember, setRemember] = useState(false);
  const form = useRef<HTMLDivElement>(null);
  // The menu the question comes from closes after it is asked, and gives the focus back to its own button: the
  // question takes it again.
  useEffect(() => {
    const focus = () => { if (!form.current?.contains(document.activeElement)) (always && !children ? dismiss : confirm).current?.focus(); };
    const timers = [0, 150, 400].map(delay => setTimeout(focus, delay));
    return () => timers.forEach(clearTimeout);
  }, []);
  function cancel() {
    const row = anchor.current?.closest(".session-row, .project-action-row")?.querySelector<HTMLElement>(":scope > button");
    onCancel();
    row?.focus();
  }
  const question = <div ref={form} className="confirm-form" role="alertdialog" aria-labelledby={`${id}-title`} aria-describedby={`${id}-detail`}
    onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); cancel(); } }}>
    <strong id={`${id}-title`}>{title}</strong>
    <span className="confirm-subject" title={subject}>{subject}</span>
    <p id={`${id}-detail`}>{detail}</p>
    {children}
    {error && <p role="alert" className="confirm-error">{error}</p>}
    {!always && <Checkbox checked={remember} disabled={busy} label={t("Do not ask again")} onChange={event => setRemember(event.currentTarget.checked)} />}
    <div className="rename-actions">
      <Button ref={dismiss} variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={cancel} />
      <Button ref={confirm} intent={intent} size="small" text={confirmLabel} loading={busy} onClick={() => onConfirm(remember)} />
    </div>
  </div>;
  return <PopoverNext isOpen content={question} placement="right-start" popoverClassName="rename-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) cancel(); }}>
    <span ref={anchor} aria-hidden="true" />
  </PopoverNext>;
}
