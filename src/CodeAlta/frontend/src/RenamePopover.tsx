import { Button, FormGroup, InputGroup, PopoverNext } from "@blueprintjs/core";
import { useEffect, useId, useRef } from "react";
import { useShellLanguage } from "./shellLanguage";

/**
 * The rename form of a row of the Explorer, in a popover beside that row. Rendered inside the row while
 * it is being renamed; the row must be positioned so the popover's anchor covers it.
 * Enter renames; Escape, Cancel or a click elsewhere leaves the name as it is.
 */
export function RenamePopover({ label, value, onChange, onSubmit, onCancel, busy, disabled = false, error = null }: {
  label: string; value: string; onChange: (value: string) => void;
  onSubmit: () => void; onCancel: () => void;
  /** A rename is on its way: the form waits for it and cannot be dismissed. */
  busy: boolean;
  /** The name cannot be changed now (for example after an unconfirmed rename). */
  disabled?: boolean;
  error?: string | null;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  // The whole name is selected, ready to be replaced or refined.
  useEffect(() => { const timer = setTimeout(() => { input.current?.focus(); input.current?.select(); }); return () => clearTimeout(timer); }, []);
  const form = <form className="rename-form" onSubmit={event => {
    event.preventDefault();
    if (!busy && !disabled && value.trim()) onSubmit();
  }}>
    <FormGroup label={label} labelFor={id} intent={error ? "danger" : "none"} helperText={error ? <span role="alert">{error}</span> : undefined}>
      <InputGroup id={id} inputRef={input} value={value} maxLength={256} disabled={busy || disabled} intent={error ? "danger" : "none"}
        autoComplete="off" spellCheck={false} onChange={event => onChange(event.target.value)} />
    </FormGroup>
    <div className="rename-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />
      <Button type="submit" intent="primary" size="small" text={t("Rename")} loading={busy} disabled={disabled || !value.trim()} />
    </div>
  </form>;
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) onCancel(); }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}
