import { useEffect, useRef, useState } from "react";
import { Alert, Button, Callout, NonIdealState } from "@blueprintjs/core";
import { appUpdate, startupConfig, type StartupConfigDocument, type StartupConfigValidation } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { CodeEditor, type CodeEditorHandle } from "./monaco/CodeEditor";
import { canSaveRecovery, recoveryStatus } from "./configRecovery";
import { maximumConfigLength } from "./configEditor";
import { ShellAppearance } from "./ShellAppearance";
import { ShellLanguageContext, useLanguagePreference, useShellLanguage } from "./shellLanguage";
import { dismissStartupScreen } from "./startupScreen";
import { availableUpdate, RecoveryUpdateNotice, type AvailableUpdate } from "./UpdateNotice";
import { useWindowTitleBar, WindowBrand, WindowControls } from "./windowChrome";
import { useWindowPreferences } from "./windowPreferences";

type Api = Pick<typeof startupConfig, "read" | "reload" | "validate" | "save" | "leave">;
type Updates = Pick<typeof appUpdate, "check" | "install" | "openReleaseNotes">;

/**
 * The window when the global configuration file cannot be loaded: there is no workspace yet, only the file
 * in an editor. It is checked as it is typed; saving a valid file starts the application.
 */
export function ConfigRecoveryScreen({ developer = false, api = startupConfig, updates = appUpdate }: { developer?: boolean; api?: Api; updates?: Updates }) {
  const language = useLanguagePreference();
  const { appearance } = useWindowPreferences();
  const titleBar = useWindowTitleBar();
  // The same theme as the workspace: this screen stands where it would.
  return <ShellLanguageContext.Provider value={language}><ShellAppearance appearance={appearance} /><div className="config-recovery-shell">
    <header className="config-recovery-titlebar" data-neoastra-drag-region><WindowBrand developer={developer} /><WindowControls snapshot={titleBar} /></header>
    <ConfigRecovery api={api} updates={updates} />
  </div></ShellLanguageContext.Provider>;
}

function ConfigRecovery({ api, updates }: { api: Api; updates: Updates }) {
  const { t, locale } = useShellLanguage();
  const [document, setDocument] = useState<StartupConfigDocument | null>(null);
  const [content, setContent] = useState("");
  const [validation, setValidation] = useState<StartupConfigValidation | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [starting, setStarting] = useState(false);
  const [confirmReload, setConfirmReload] = useState(false);
  const editor = useRef<CodeEditorHandle | null>(null);
  const shownError = useRef(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  // A file written by a newer version is the usual reason an installed one cannot read it, and this screen is
  // then all the application shows: it says when a newer version is published, and installs it.
  const [update, setUpdate] = useState<AvailableUpdate | null>(null);
  const [updateFailure, setUpdateFailure] = useState(false);
  const updating = useRef(false);
  useEffect(() => {
    const abort = new AbortController();
    void updates.check({}, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(value => { if (!abort.signal.aborted) setUpdate(availableUpdate(value)); }, () => { /* No check: the file is still there to repair. */ });
    return () => abort.abort();
  }, [updates]);
  function installUpdate() {
    if (updating.current) return;
    updating.current = true; setUpdateFailure(false);
    void updates.install({}, { timeoutMilliseconds: 30_000 }).then(reply => reply.status === "started", () => false).then(started => {
      if (started || !alive.current) return;
      updating.current = false; setUpdateFailure(true);
    });
  }

  function show(value: StartupConfigDocument) {
    setDocument(value); setFailure(null);
    setContent(value.content ?? ""); setValidation(value.validation);
    shownError.current = false;
  }
  useEffect(() => {
    const abort = new AbortController();
    void api.read({}, { signal: abort.signal, timeoutMilliseconds: 15_000 })
      .then(value => { if (!abort.signal.aborted) show(value); },
        () => { if (!abort.signal.aborted) setDocument({ status: "unreadable", path: "", content: null, failure: null, validation: null }); })
      .finally(dismissStartupScreen);
    return () => abort.abort();
  }, [api]);

  // Checked shortly after typing stops; a result for older text is dropped.
  const baseline = document?.status === "ok" ? document.content ?? "" : null;
  useEffect(() => {
    if (baseline === null || content === baseline && document?.validation) { if (baseline !== null) setValidation(document!.validation); return; }
    setValidation(null);
    if (content.length > maximumConfigLength) return;
    const abort = new AbortController();
    const timer = window.setTimeout(() => {
      void api.validate({ content }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
        .then(value => { if (!abort.signal.aborted) setValidation(value); }, () => { /* Saving stays off until the next check answers. */ });
    }, 250);
    return () => { window.clearTimeout(timer); abort.abort(); };
  }, [api, baseline, content, document]);

  const status = recoveryStatus(document, content, validation, failure);
  // The caret starts where the file is wrong.
  useEffect(() => {
    if (shownError.current || !status.position || !editor.current) return;
    shownError.current = true;
    editor.current.reveal(status.position.line, status.position.column);
  }, [status.position]);

  const dirty = baseline !== null && content !== baseline;
  const savable = canSaveRecovery(document, content, validation, busy);
  async function save() {
    if (!savable) return;
    setBusy(true); setFailure(null);
    try {
      const result = await api.save({ content }, { timeoutMilliseconds: 30_000 });
      if (!alive.current) return;
      if (result.status === "ok") setStarting(true); // The host loads the application into this window next.
      else if (result.status === "invalid") setValidation(result.validation);
      else setFailure(result.failure ?? t("The configuration was not saved."));
    } catch { if (alive.current) setFailure(t("The save did not complete; reload to see what is on disk.")); }
    finally { if (alive.current) setBusy(false); }
  }
  async function reload() {
    setConfirmReload(false); setBusy(true);
    try { const value = await api.reload({}, { timeoutMilliseconds: 15_000 }); if (alive.current) show(value); }
    catch { if (alive.current) setFailure(t("The configuration file could not be read.")); }
    finally { if (alive.current) setBusy(false); }
  }
  function exit() { void api.leave({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The window's close button still ends the application. */ }); }
  // Ctrl+S saves from anywhere on the screen, as in the editor.
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.key.toLowerCase() !== "s" || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.defaultPrevented) return;
      event.preventDefault();
      void save();
    };
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  });

  if (starting) return <main className="config-recovery"><NonIdealState icon={<ActivitySpinner size={28} />} title={t("Starting CodeAlta…")} /></main>;
  const marker = status.position ? { ...status.position, message: status.detail ?? "" } : null;
  return <main className="config-recovery" aria-labelledby="config-recovery-title">
    <header className="config-recovery-heading">
      <span className="config-recovery-icon"><AppIcon name="error" size={26} /></span>
      <div><h1 id="config-recovery-title">{t("CodeAlta could not load your configuration")}</h1>
        <p>{t("Repair the file and save it to continue.")}</p>
        {document?.path && <code className="config-recovery-path">{document.path}</code>}</div>
    </header>
    {update && <RecoveryUpdateNotice update={update} locale={locale} failed={updateFailure} onInstall={installUpdate}
      onOpenReleaseNotes={() => { void updates.openReleaseNotes({}, { timeoutMilliseconds: 15_000 }).catch(() => { /* The command is on the screen. */ }); }} />}
    <div className="config-editor-surface config-recovery-editor">
      {!document ? <NonIdealState icon={<ActivitySpinner size={28} />} title={t("Reading configuration…")} />
        : baseline === null ? <NonIdealState icon={<AppIcon name="config" size={36} />} title={t(status.key, status.parameters)} description={status.detail ?? undefined} />
        : <CodeEditor value={content} onChange={value => { setContent(value); setFailure(null); }} language="ini" label={t("Configuration file")}
            readOnly={busy} marker={marker} onSave={() => void save()} handle={editor} />}
    </div>
    <footer className="config-recovery-footer">
      <Callout compact intent={status.intent} className="config-recovery-status" role={status.intent === "danger" ? "alert" : "status"}
        icon={<AppIcon name={status.intent === "success" ? "checked" : status.intent === "danger" ? "error" : "info"} size={16} />}>
        {status.position
          ? <button type="button" className="config-recovery-position" title={t("Go to the error")}
              onClick={() => editor.current?.reveal(status.position!.line, status.position!.column)}>{t(status.key, status.parameters)}</button>
          : <strong>{t(status.key, status.parameters)}</strong>}
        {status.detail && <span className="config-editor-diagnostic">{status.detail}</span>}
      </Callout>
      <div className="config-recovery-actions">
        <Button icon={<AppIcon name="close" size={15} />} onClick={exit}>{t("Exit")}</Button>
        <span className="provider-settings-spacer" />
        {busy && <ActivitySpinner size={14} />}
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={busy || !document} title={t("Discard edits and read the file again")}
          onClick={() => dirty ? setConfirmReload(true) : void reload()}>{t("Reload")}</Button>
        <Button intent="primary" icon={<AppIcon name="save" size={15} />} disabled={!savable} title="Ctrl+S" onClick={() => void save()}>{t("Save and continue")}</Button>
      </div>
    </footer>
    <Alert isOpen={confirmReload} intent="danger" cancelButtonText={t("Cancel")} confirmButtonText={t("Discard and reload")}
      canEscapeKeyCancel canOutsideClickCancel onCancel={() => setConfirmReload(false)} onConfirm={() => void reload()}>
      <p>{t("Reload reads the file again and discards the edits made here.")}</p>
    </Alert>
  </main>;
}
