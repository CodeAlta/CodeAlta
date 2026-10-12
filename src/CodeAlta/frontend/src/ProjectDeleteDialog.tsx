import { useLayoutEffect, useRef } from "react";
import { Button } from "@blueprintjs/core";
import { AppWindowSurface } from "./AppWindow";
import { useShellLanguage } from "./shellLanguage";
import type { ProjectDeleteTarget } from "./projectDeletion";

/** Always asks; Cancel is focused so Enter never deletes by default. */
export function ProjectDeleteDialog({ target, busy, disabled, error, onConfirm, onCancel }: {
  target: ProjectDeleteTarget; busy: boolean; disabled: boolean; error: string;
  onConfirm: () => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const cancel = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => { dialog.current?.showModal(); cancel.current?.focus(); }, []);
  return <dialog ref={dialog} className="app-dialog session-info-dialog project-delete-dialog" aria-modal="true" aria-labelledby="project-delete-title"
    onCancel={event => { event.preventDefault(); if (!busy) onCancel(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.project-delete.v1" title={t("Delete project?")} titleId="project-delete-title"
      preferredSize={viewport => ({ width: Math.min(600, viewport.width - 40), height: Math.min(340, viewport.height - 40) })}
      onClose={() => { if (!busy) onCancel(); }} closeLabel={t("Cancel")}>
      <p><strong>{target.name}</strong></p>
      <p><code>{target.path}</code></p>
      <p>{t("This removes the project from CodeAlta and permanently deletes all its sessions, including sub-agents. The project folder, source files and git worktrees are kept.")}</p>
      <p>{t("This cannot be undone. Running sessions or pending work must finish first.")}</p>
      {error && <p role="alert" className="error-text">{error}</p>}
      <div className="dialog-actions">
        <Button ref={cancel} autoFocus disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
        <Button intent="danger" loading={busy} disabled={disabled || busy} onClick={onConfirm}>{t("Delete project and sessions")}</Button>
      </div>
    </AppWindowSurface>
  </dialog>;
}
