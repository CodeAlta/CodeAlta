import { useLayoutEffect, useRef, type ReactNode } from "react";
import { useShellLanguage } from "./shellLanguage";

export function RemindersDialog({ children, onClose }: { children: ReactNode; onClose: () => void }) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    if (element?.isConnected && element.matches(":modal")) close.current?.focus();
    return () => { if (element?.open) element.close(); };
  }, []);
  return <dialog ref={dialog} className="app-dialog reminders-dialog" aria-modal="true" aria-labelledby="reminders-dialog-title"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) composing.current = true;
      else onClose();
    }} onKeyUp={() => { composing.current = false; }} onCompositionEnd={() => { composing.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}>
    <header><h2 id="reminders-dialog-title">{t("Reminders")}</h2><button ref={close} type="button" className="quiet-button" onClick={onClose}>{t("Close")}</button></header>
    {children}
  </dialog>;
}
