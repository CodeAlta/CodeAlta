import { Button, FormGroup, InputGroup, PopoverNext } from "@blueprintjs/core";
import { useEffect, useId, useRef, useState } from "react";
import { useShellLanguage } from "./shellLanguage";

type RenameDraft = { initialValue: string; value?: never; onChange?: never }
  | { initialValue?: never; value: string; onChange: (value: string) => void };

/**
 * The rename form of a row of the Explorer, in a popover beside that row. Rendered inside the row while
 * it is being renamed; the row must be positioned so the popover's anchor covers it.
 * Enter renames; Escape, Cancel or a click elsewhere leaves the name as it is.
 */
export function RenamePopover({ label, initialValue, value, onChange, onSubmit, onCancel, busy, disabled = false, error = null }: RenameDraft & {
  label: string;
  onSubmit: (value: string) => void; onCancel: () => void;
  /** A rename is on its way: the form waits for it and cannot be dismissed. */
  busy: boolean;
  /** The name cannot be changed now (for example after an unconfirmed rename). */
  disabled?: boolean;
  error?: string | null;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [draft, setDraft] = useState(initialValue ?? "");
  const name = value ?? draft;
  const input = useRef<HTMLInputElement>(null);
  // The whole name is selected, ready to be replaced or refined.
  useEffect(() => { const timer = setTimeout(() => { input.current?.focus(); input.current?.select(); }); return () => clearTimeout(timer); }, []);
  const form = <form className="rename-form" onSubmit={event => {
    event.preventDefault();
    if (!busy && !disabled && name.trim()) onSubmit(name);
  }}>
    <FormGroup label={label} labelFor={id} intent={error ? "danger" : "none"} helperText={error ? <span role="alert">{error}</span> : undefined}>
      <InputGroup id={id} inputRef={input} value={name} maxLength={256} disabled={busy || disabled} intent={error ? "danger" : "none"}
        autoComplete="off" spellCheck={false} onChange={event => (onChange ?? setDraft)(event.target.value)} />
    </FormGroup>
    <div className="rename-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />
      <Button type="submit" intent="primary" size="small" text={t("Rename")} loading={busy} disabled={disabled || !name.trim()} />
    </div>
  </form>;
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) onCancel(); }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}
