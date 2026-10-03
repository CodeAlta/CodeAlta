import { AppWindowSurface } from "./AppWindow";
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
    <AppWindowSurface storageKey="codealta.desktop.window.reminders.v1" title={t("Reminders")} titleId="reminders-dialog-title" preferredSize={viewport => ({ width: Math.min(920, viewport.width - 40), height: Math.min(700, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={close}>
    {children}
  </AppWindowSurface></dialog>;
}
