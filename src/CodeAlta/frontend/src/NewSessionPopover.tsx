import { Button, FormGroup, InputGroup, PopoverNext } from "@blueprintjs/core";
import { useEffect, useId, useRef, type ReactNode } from "react";
import { useShellLanguage } from "./shellLanguage";

/**
 * The form of a new session, in a popover beside the row of the Explorer it is created in: a project, or the
 * chats. Rendered inside that row, as its rename form is; the row must be positioned so the popover's anchor
 * covers it. Enter in the title creates; Escape, Cancel or a click elsewhere creates nothing and gives the focus
 * back to the row.
 */
export function NewSessionPopover({ heading, title, onTitle, choices, place, onCreate, onCancel, busy, disabled = false, error = null, refresh }: {
  /** What is created, and where: a session in a project, a chat. */
  heading: string;
  title: string; onTitle: (value: string) => void;
  /** The chips of the agent, the provider, the model and the reasoning. */
  choices: ReactNode;
  /** For a project: the chips of where the session works. */
  place?: ReactNode;
  onCreate: () => void; onCancel: () => void;
  /** A creation is on its way: the form waits for it and cannot be dismissed. */
  busy: boolean;
  /** Nothing can be created now: the choices are still read, or an earlier creation is unconfirmed. */
  disabled?: boolean;
  /** Why the session was not created. */
  error?: string | null;
  /** What reads again what the form or its error is about: the choices, the list of sessions. */
  refresh?: Readonly<{ label: string; run: () => void }>;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const anchor = useRef<HTMLSpanElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const content = useRef<HTMLFormElement>(null);
  useEffect(() => { const timer = setTimeout(() => input.current?.focus()); return () => clearTimeout(timer); }, []);
  function cancel() {
    const row = anchor.current?.closest(".project-action-row")?.querySelector<HTMLElement>(":scope > button");
    onCancel();
    row?.focus();
  }
  const form = <form ref={content} className="rename-form new-session-form" role="dialog" aria-labelledby={`${id}-heading`} onSubmit={event => {
    event.preventDefault();
    if (!busy && !disabled) onCreate();
  }}>
    <strong id={`${id}-heading`} title={heading}>{heading}</strong>
    <FormGroup label={t("Title (optional)")} labelFor={id}>
      <InputGroup id={id} inputRef={input} value={title} maxLength={256} disabled={busy} autoComplete="off" spellCheck={false}
        onChange={event => onTitle(event.target.value)} />
    </FormGroup>
    {/* Nothing of the form is changed while its session is created. */}
    <div className="new-session-choices" inert={busy}>{choices}{place}</div>
    {error && <p role="alert" className="new-session-error">{error}</p>}
    <div className="rename-actions">
      {refresh && <Button className="new-session-refresh" variant="minimal" size="small" text={refresh.label} disabled={busy} onClick={refresh.run} />}
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={cancel} />
      <Button type="submit" intent="primary" size="small" text={t("Create")} loading={busy} disabled={disabled} />
    </div>
  </form>;
  // The pickers of the chips are popovers of their own, opened after this one: Blueprint keeps this one open for
  // a click in them, and gives them Escape once the focus is in them. In the moment before that, Escape comes
  // here: with a picker open it is not for the form, which stays with what was typed in it.
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover new-session-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={(next, event) => {
      const picker = (event?.nativeEvent ?? event) instanceof KeyboardEvent && !!content.current?.querySelector(".bp6-popover-open");
      if (!next && !busy && !picker) cancel();
    }}>
    <span ref={anchor} aria-hidden="true" />
  </PopoverNext>;
}
