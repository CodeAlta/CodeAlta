import { useRef, type ReactNode } from "react";
import { Button, FormGroup, InputGroup } from "@blueprintjs/core";
import { AppWindow } from "./AppWindow";
import { useShellLanguage } from "./shellLanguage";

/** Creation UI only; the shell owns the captured choices and correlated creation lifetime. */
export function NewSessionDialog({ projectName, title, onTitle, choices, place, disabled, busy, notice, onCreate, onClose }: {
  projectName: string | null; title: string; onTitle: (value: string) => void; choices: ReactNode; place?: ReactNode;
  disabled: boolean; busy: boolean; notice?: ReactNode; onCreate: () => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  return <AppWindow storageKey="codealta.desktop.window.newSession.v1" className="new-session-dialog" titleId="new-session-title"
    title={projectName ? t("New session in {name}", { name: projectName }) : t("New chat")} closeLabel={t("Cancel")}
    preferredSize={() => ({ width: 640, height: 340 })} minimumSize={{ width: 360, height: 300 }} keepOnOutsidePress
    onOpened={() => input.current?.focus()} onClose={onClose}
    onKeyDown={event => {
      // An open picker owns Escape first. Otherwise close explicitly, including from text fields.
      if (event.key !== "Escape" || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat
        || event.currentTarget.querySelector(".bp6-overlay-open")) return;
      event.preventDefault(); event.stopPropagation(); if (!busy) onClose();
    }}
    onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <form className="new-session-form" onSubmit={event => { event.preventDefault(); if (!disabled) onCreate(); }}>
      <FormGroup label={t("Title (optional)")} labelFor="new-session-name">
        <InputGroup inputRef={input} id="new-session-name" value={title} maxLength={256} disabled={busy}
          onChange={event => onTitle(event.currentTarget.value)} />
      </FormGroup>
      <div className="new-session-choices">{choices}</div>
      {place && <div className="new-session-place">{place}</div>}
      {notice}
      <div className="dialog-actions">
        <Button type="button" disabled={busy} onClick={onClose}>{t("Cancel")}</Button>
        <Button type="submit" intent="primary" disabled={disabled} loading={busy}>{t("Create and open")}</Button>
      </div>
    </form>
  </AppWindow>;
}
