import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, NonIdealState, Switch } from "@blueprintjs/core";
import { projectFiles } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { CodeEditor, type CodeEditorHandle } from "./CodeEditor";
import { fileAppearance } from "./fileAppearance";
import { canSaveFile, fileConflictDismissed, fileEdited, fileLoaded, fileLoading, fileSaved, fileSaveUnknown, fileSaving, fileStatus,
  initialFileEditorState, maximumFileLength } from "./fileEditorState";
import type { FileEditors } from "./fileEditors";
import { fileLanguage } from "./fileLanguage";
import { fileTabKey, fileTabName, type FileTab } from "./fileTabs";
import { useShellLanguage } from "./shellLanguage";

const modalOpen = () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');

/** The question asked before unsaved edits are dropped: by closing the tab (Save / Discard / Cancel) or by reloading the file. */
export function UnsavedFileDialog({ name, mode, busy = false, onSave, onDiscard, onCancel }: {
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
  return <Dialog isOpen className="unsaved-file-dialog" title={t("Unsaved changes")} isCloseButtonShown={false} canOutsideClickClose={false} onClose={onCancel}>
    <DialogBody>{t("Save the changes to {name} before exiting?", { name: names.join(", ") })}</DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="danger" disabled={busy} onClick={onDiscard}>{t("Exit without saving")}</Button>
      <Button intent="primary" loading={busy} autoFocus onClick={onSave}>{t("Save all")}</Button>
    </>} />
  </Dialog>;
}

/**
 * The editor of one project file in a tab: read through `projectFiles.read`, edited in Monaco with the
 * language of its extension, and saved (Ctrl+S) against the revision that was read. A file changed on
 * disk in the meantime is not replaced without a choice between reloading it and overwriting it.
 */
export function ProjectFileEditor({ tab, epoch, visible, active, editors, onActivate, api = projectFiles }: {
  tab: FileTab;
  /** The owned host's epoch; null without an owned host, undefined until the host has answered. */
  epoch: string | null | undefined;
  /** The tab is the selected one of its pane. A restored tab reads its file the first time it is shown. */
  visible: boolean;
  /** This tab is the one commands and the keyboard act on. */
  active: boolean; editors: FileEditors; onActivate: () => void;
  api?: Pick<typeof projectFiles, "read" | "write">;
}) {
  const { t } = useShellLanguage();
  const [state, setState] = useState(initialFileEditorState);
  const [generation, setGeneration] = useState(0);
  const [wrap, setWrap] = useState(true);
  const [position, setPosition] = useState({ line: 1, column: 1 });
  const [reloadAsked, setReloadAsked] = useState(false);
  const [shown, setShown] = useState(visible);
  useEffect(() => { if (visible) setShown(true); }, [visible]);
  const alive = useRef(true);
  const loaded = useRef(-1);
  const writing = useRef(false);
  const editor = useRef<CodeEditorHandle | null>(null);
  const latest = useRef({ state, epoch }); latest.current = { state, epoch };
  const key = fileTabKey(tab);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  // Read on open and on an explicit reload. A file already shown is not read again when the host changes.
  useEffect(() => {
    if (!shown || epoch === undefined || loaded.current === generation) return;
    const refused = (status: string) => fileLoaded({ status, content: null, revision: null, readOnly: false });
    if (epoch === null) { setState(refused("unavailable")); return; }
    const controller = new AbortController();
    setState(fileLoading);
    void api.read({ expectedEpoch: epoch, projectId: tab.projectId, path: tab.path }, { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.status === "ok") loaded.current = generation;
      setState(fileLoaded(value)); setPosition({ line: 1, column: 1 });
    }).catch(() => { if (!controller.signal.aborted) setState(refused("read_failed")); });
    return () => controller.abort();
  }, [api, epoch, generation, shown, tab.projectId, tab.path]);

  async function save(overwrite = false): Promise<boolean> {
    const { state: current, epoch: host } = latest.current;
    if (writing.current || !host || !current.baseline) return false;
    if (current.content.length > maximumFileLength) { setState(value => fileSaved(value, "", { status: "too_large", revision: null })); return false; }
    // Nothing to write: the file already holds this text.
    if (!canSaveFile(current, overwrite)) return current.phase === "ready" && !current.dirty && !current.conflict && !current.saving;
    const submitted = current.content;
    writing.current = true;
    setState(fileSaving);
    try {
      const result = await api.write({ expectedEpoch: host, projectId: tab.projectId, path: tab.path, content: submitted,
        expectedRevision: current.baseline.revision, overwrite }, { timeoutMilliseconds: 30000 });
      if (!alive.current) return false;
      setState(value => fileSaved(value, submitted, result));
      return result.status === "ok" && !!result.revision;
    } catch {
      if (alive.current) setState(fileSaveUnknown);
      return false;
    } finally { writing.current = false; }
  }
  const saveLatest = useRef(save); saveLatest.current = save;
  useEffect(() => editors.attach(key, () => saveLatest.current()), [editors, key]);
  useEffect(() => { editors.setDirty(key, state.dirty); }, [editors, key, state.dirty]);

  function reload() { setReloadAsked(false); setGeneration(value => value + 1); }
  const ready = state.phase === "ready";
  // The active tab takes the keyboard, unless a window is open over the workspace.
  useEffect(() => { if (active && ready && !modalOpen()) editor.current?.focus(); }, [active, ready]);

  // Ctrl+S from the footer or a notice; inside the editor Monaco handles it first.
  function keyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.key.toLowerCase() !== "s" || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey
      || event.nativeEvent.isComposing) return;
    event.preventDefault(); event.stopPropagation();
    void save();
  }

  const look = fileAppearance(tab.path, false);
  const name = fileTabName(tab);
  const busy = state.saving || state.loading;
  return <section className="file-editor" data-active={active} aria-label={tab.path} onFocusCapture={onActivate} onPointerDownCapture={onActivate} onKeyDown={keyDown}>
    {state.conflict && <Callout className="file-editor-notice" intent="warning" compact role="alert">
      <span>{t("The file changed on disk since it was opened.")}</span>
      <span className="file-editor-notice-actions">
        <Button size="small" disabled={busy} onClick={reload}>{t("Reload")}</Button>
        <Button size="small" intent="warning" disabled={!canSaveFile(state, true)} onClick={() => void save(true)}>{t("Overwrite")}</Button>
        <Button size="small" variant="minimal" disabled={busy} onClick={() => setState(fileConflictDismissed)}>{t("Cancel")}</Button>
      </span>
    </Callout>}
    {!state.conflict && state.notice && <Callout className="file-editor-notice" intent={state.notice.intent} compact role="alert">{t(state.notice.key)}</Callout>}
    {ready ? <div className="file-editor-surface"><CodeEditor value={state.content} onChange={text => setState(value => fileEdited(value, text))}
        language={fileLanguage(tab.path)} label={tab.path} readOnly={state.readOnly || busy} wrap={wrap} handle={editor}
        onSave={() => void save()} onCursor={(line, column) => setPosition({ line, column })} /></div>
      : state.phase === "loading" ? <NonIdealState className="file-editor-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
      : <NonIdealState className="file-editor-empty" icon={<AppIcon name={look.icon} size={36} />} title={name}
        description={t(state.failure ?? "The file could not be read.")}
        action={<Button icon={<AppIcon name="refresh" size={15} />} disabled={!epoch} onClick={reload}>{t("Reload")}</Button>} />}
    <footer className="file-editor-footer">
      <span className="file-editor-status" data-state={state.conflict ? "conflict" : state.dirty ? "modified" : "clean"} role="status">
        {busy && <ActivitySpinner size={12} />}{t(fileStatus(state))}</span>
      {ready && <span className="file-editor-position">{t("Ln {line}, Col {column}", position)}</span>}
      <span className="file-editor-path" title={tab.path}><span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={13} /></span><span>{tab.path}</span></span>
      <Switch className="file-editor-wrap" checked={wrap} label={t("Wrap lines")} onChange={event => setWrap(event.currentTarget.checked)} />
      <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Reload")} title={t("Reload")}
        disabled={busy || !epoch} onClick={() => { if (state.dirty) setReloadAsked(true); else reload(); }} />
      <Button variant="minimal" size="small" icon={<AppIcon name="save" size={14} />} disabled={!canSaveFile(state)}
        title={`${t("Save")} (Ctrl+S)`} onClick={() => void save()}>{t("Save")}</Button>
    </footer>
    {reloadAsked && <UnsavedFileDialog name={name} mode="reload" onDiscard={reload} onCancel={() => setReloadAsked(false)} />}
  </section>;
}
