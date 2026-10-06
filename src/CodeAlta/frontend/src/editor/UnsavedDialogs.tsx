import { useRef } from "react";
import { Button, Dialog, DialogBody, DialogFooter } from "@blueprintjs/core";
import { frontLayer } from "../frontLayer";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";

/** The question asked before unsaved edits are dropped: by closing files (Save / Discard / Cancel) or by reloading one. */
export function UnsavedFileDialog({ name, mode, busy = false, onSave, onDiscard, onCancel }: {
  /** The file, or the files separated by commas, that hold the edits. */
  name: string; mode: "close" | "reload"; busy?: boolean; onSave?: () => void; onDiscard: () => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  return <Dialog isOpen className="unsaved-file-dialog" title={t("Unsaved changes")} isCloseButtonShown={false} canOutsideClickClose={false} onClose={onCancel}>
    <DialogBody>{mode === "close" ? t("Save the changes to {name} before closing?", { name }) : t("Discard the changes to {name} and reload it?", { name })}</DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" disabled={busy} autoFocus={mode === "reload"} onClick={onDiscard}>{t(mode === "close" ? "Discard" : "Reload")}</Button>
      {mode === "close" && <Button intent="primary" loading={busy} autoFocus onClick={onSave}>{t("Save")}</Button>}
    </>} />
  </Dialog>;
}

/** The question asked before exiting while files hold unsaved edits (Save all / Exit without saving / Cancel). */
export function UnsavedExitDialog({ names, busy = false, onSave, onDiscard, onCancel }: {
  names: readonly string[]; busy?: boolean; onSave: () => void; onDiscard: () => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  // An exit can be asked for while a window of the application is open: the question is shown in front of it.
  const layer = useRef(frontLayer());
  return <Dialog isOpen className="unsaved-file-dialog" title={t("Unsaved changes")} isCloseButtonShown={false} canOutsideClickClose={false}
    portalContainer={layer.current} onClose={onCancel}>
    <DialogBody>{t("Save the changes to {name} before exiting?", { name: names.join(", ") })}</DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" disabled={busy} onClick={onDiscard}>{t("Exit without saving")}</Button>
      <Button intent="primary" loading={busy} autoFocus onClick={onSave}>{t("Save all")}</Button>
    </>} />
  </Dialog>;
}

/**
 * What the question before a deletion says: that the trash did not take the entry, the question itself, what
 * becomes of unsaved edits, and the name of the button. Windows has a Recycle Bin, the other systems a Trash.
 */
export function deletionWording({ directory, permanent, trashFailed, unsaved, platform }: Readonly<{
  directory: boolean; permanent: boolean; trashFailed: boolean; unsaved: number; platform: string }>):
  Readonly<{ failed: MessageKey | null; question: MessageKey; unsaved: MessageKey | null; action: MessageKey }> {
  const bin = platform === "windows";
  return {
    failed: !trashFailed ? null : bin ? "It could not be moved to the Recycle Bin." : "It could not be moved to the Trash.",
    question: permanent ? directory ? "Delete the folder {name} and everything in it permanently? This cannot be undone." : "Delete {name} permanently? This cannot be undone."
      : bin ? directory ? "Move the folder {name} and everything in it to the Recycle Bin?" : "Move {name} to the Recycle Bin?"
      : directory ? "Move the folder {name} and everything in it to the Trash?" : "Move {name} to the Trash?",
    unsaved: unsaved <= 0 ? null : unsaved === 1 ? "The unsaved changes of 1 open file will be lost." : "The unsaved changes of {count} open files will be lost.",
    action: permanent ? "Delete permanently" : bin ? "Move to Recycle Bin" : "Move to Trash",
  };
}

/** The question asked before a file or a folder is deleted: moved to the trash of the system, or removed for good. */
export function DeleteEntryDialog({ name, directory, permanent, trashFailed, unsaved, platform, busy, onDelete, onCancel }: {
  name: string; directory: boolean;
  /** The entry is removed for good: the system has no trash, or the trash could not take it. */
  permanent: boolean; trashFailed: boolean;
  /** How many open files with unsaved edits the deletion closes. */
  unsaved: number; platform: string; busy: boolean; onDelete: () => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const wording = deletionWording({ directory, permanent, trashFailed, unsaved, platform });
  return <Dialog isOpen className="unsaved-file-dialog" title={t(directory ? "Delete folder" : "Delete file")} isCloseButtonShown={false} canOutsideClickClose={!busy} onClose={onCancel}>
    <DialogBody>
      {wording.failed && <p>{t(wording.failed)}</p>}
      <p>{t(wording.question, { name })}</p>
      {wording.unsaved && <p>{t(wording.unsaved, { count: unsaved })}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" loading={busy} autoFocus onClick={onDelete}>{t(wording.action)}</Button>
    </>} />
  </Dialog>;
}
