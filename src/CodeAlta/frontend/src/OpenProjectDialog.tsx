import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { canImportCheckedFolder, projectOpeningMessage, type createProjectOpening } from "./projectOpening";
import { savedProjectSelection, type SavedProjectIdentity } from "./savedProjectSelection";
import { Button, InputGroup, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { folderCompletionRequest, projectFolderCompletion } from "./directoryCompletion";
import { sameFolder, type FolderPick } from "./folderPicker";
import { useShellLanguage } from "./shellLanguage";
import { workflowNotice, type WorkflowNotice } from "./workflowNotice";
import { modalDialogOpen } from "./modalDialogs";

export function OpenProjectDialog({ snapshot, getCurrentSnapshot, epoch, getCurrentEpoch, getCurrentScope, allowCompletion, capability, opening,
  completeDirectory, initialFolder, pickFolder, onOpen, onRefresh, onImported, onClose }: {
  snapshot: WorkspaceSnapshot | undefined; getCurrentSnapshot: () => WorkspaceSnapshot | undefined; epoch: string | undefined;
  getCurrentEpoch: () => string | undefined;
  getCurrentScope: () => { projectId: string | null; sessionId: string | null };
  allowCompletion: boolean;
  capability: ReturnType<typeof createMutationCapability> | undefined;
  opening: ReturnType<typeof createProjectOpening>;
  completeDirectory: (request: WorkspaceDirectoryCompletionRequest, options: { signal: AbortSignal }) => Promise<WorkspaceDirectoryCompletionResponse>;
  /** A folder chosen before the window opened: it is checked at once, ready to be trusted and opened. */
  initialFolder?: string;
  /** Shows the operating system's folder dialog, starting in the given folder; absent where there is none. */
  pickFolder?: (initialDirectory: string | null) => Promise<FolderPick>;
  onOpen: (project: SavedProjectIdentity) => boolean; onRefresh: (signal: AbortSignal) => Promise<{ configured: boolean } | undefined>;
  onImported: (id: string, path: string, signal: AbortSignal) => Promise<boolean>;
  onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  // One field: a saved project's name or path, or the absolute path of a folder to add.
  const [query, setQuery] = useState(initialFolder ?? "");
  const filter = query;
  const [active, setActive] = useState(0);
  const [preview, setPreview] = useState<{ requestedPath: string; path: string }>();
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<WorkflowNotice>("");
  const [notice, setNotice] = useState<WorkflowNotice>("");
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [suggestBusy, setSuggestBusy] = useState(false);
  const [suggestMessage, setSuggestMessage] = useState<WorkflowNotice>("");
  const [suggestions, setSuggestions] = useState<{ request: WorkspaceDirectoryCompletionRequest; paths: string[];
    revision: number; scope: { projectId: string | null; sessionId: string | null } }>();
  const [suggestActive, setSuggestActive] = useState(0);
  const [, notifyCapability] = useState(0);
  const importEvidence = useSyncExternalStore(opening.subscribe, opening.getSnapshot);
  const canImport = !!epoch && !!capability?.canMutate();
  const alive = useRef(true);
  const origin = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);
  const busyNow = useRef(false);
  const draftNow = useRef(initialFolder ?? "");
  const picking = useRef(false);
  const editRevision = useRef(0);
  const suggestWork = useRef<AbortController | null>(null);
  const results = useRef<HTMLDivElement>(null);
  const followUp = useRef(new AbortController());
  useEffect(() => {
    alive.current = true;
    followUp.current = new AbortController();
    return () => { alive.current = false; followUp.current.abort(); suggestWork.current?.abort(); suggestWork.current = null; };
  }, []);
  useEffect(() => {
    return capability?.subscribe(() => notifyCapability(value => value + 1));
  }, [capability]);
  const restoreFocus = () => requestAnimationFrame(() => {
    if (origin.current?.isConnected && !modalDialogOpen()) origin.current.focus();
  });
  const close = () => {
    alive.current = false; followUp.current.abort(); suggestWork.current?.abort(); suggestWork.current = null; onClose();
  };
  // Cancellation returns to the opener; successful navigation focuses the new workspace instead.
  const dismiss = () => { close(); restoreFocus(); };
  // A trailing separator names the same folder: "C:\code\App\" still matches the project at "C:\code\App".
  const normalized = filter.trim().toLowerCase().replace(/[\\/]$/u, "");
  const matches = (snapshot?.projects ?? []).filter(project => !normalized || project.name.toLowerCase().includes(normalized)
    || project.path.toLowerCase().includes(normalized))
    .sort((a, b) => a.name.toLowerCase() < b.name.toLowerCase() ? -1 : a.name.toLowerCase() > b.name.toLowerCase() ? 1
      : a.id < b.id ? -1 : a.id > b.id ? 1 : a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  // Sorted saved projects first, then the folders suggested for a typed path. A path that ends with a
  // separator names a folder itself: it comes first, so Enter opens it rather than one of its children.
  // Folders that are saved projects already are listed as projects only.
  const typedFolder = query.trim().replace(/[\\/]$/u, "");
  const folders = () => {
    if (!visibleSuggestions) return [];
    const saved = new Set((snapshot?.projects ?? []).map(project => project.path.toLowerCase()));
    const children = visibleSuggestions.paths.filter(path => !saved.has(path.toLowerCase()));
    return /[\\/]$/u.test(query) && !saved.has(typedFolder.toLowerCase()) ? [typedFolder, ...children] : children;
  };
  const sameScope = (scope: { projectId: string | null; sessionId: string | null }) => {
    const now = getCurrentScope();
    return now.projectId === scope.projectId && now.sessionId === scope.sessionId;
  };
  const maySuggest = () => alive.current && allowCompletion && !!epoch && getCurrentEpoch() === epoch && !!capability?.canMutate()
    && !opening.getSnapshot() && !busyNow.current && !busy;
  const visibleSuggestions = suggestions && editRevision.current === suggestions.revision && !suggestWork.current
    && maySuggest() && sameScope(suggestions.scope) && draftNow.current === query ? suggestions : undefined;
  function clearSuggestions() {
    suggestWork.current?.abort();
    suggestWork.current = null;
    setSuggestBusy(false);
    setSuggestions(undefined);
    setSuggestActive(0);
    setSuggestMessage("");
  }
  const itemCount = matches.length + folders().length;
  const index = Math.min(active, itemCount - 1);
  const activeFolder = index >= matches.length ? folders()[index - matches.length] : undefined;
  const scopeAtRender = getCurrentScope();
  const completionAuthority = !!epoch && getCurrentEpoch() === epoch && !!capability?.canMutate();
  useEffect(() => {
    // A host or selected target transition invalidates even an ABA return to the old identity.
    editRevision.current++;
    clearSuggestions();
  }, [epoch, scopeAtRender.projectId, scopeAtRender.sessionId, allowCompletion, completionAuthority, !!importEvidence]);
  // Folder suggestions follow the typed path; a path that cannot be completed simply has none.
  async function suggest(retry = true) {
    if (!maySuggest() || suggestWork.current) return;
    const request = folderCompletionRequest(draftNow.current, epoch!);
    clearSuggestions();
    if (typeof request === "string") return;
    const scope = getCurrentScope();
    const revision = editRevision.current;
    const controller = new AbortController();
    suggestWork.current = controller;
    setSuggestBusy(true);
    const current = () => alive.current && suggestWork.current === controller && !controller.signal.aborted
      && maySuggest() && getCurrentEpoch() === request.expectedHostEpoch && sameScope(scope)
      && revision === editRevision.current && draftNow.current === query;
    try {
      const reply = await completeDirectory(request, { signal: controller.signal });
      if (!current()) return;
      const projected = projectFolderCompletion(reply, request);
      if (!projected) { setSuggestMessage({ key: "Folder suggestions returned an invalid or foreign response. Request again explicitly if needed." }); return; }
      if (projected.status === "complete" || projected.status === "incomplete") {
        setSuggestions({ request, paths: projected.directories, revision, scope });
      } else if (projected.status === "busy" && retry) {
        // The host reads one directory at a time: ask once more when the previous read is over.
        window.setTimeout(() => { if (alive.current && revision === editRevision.current) void suggest(false); }, 400);
      } else if (projected.status === "denied" || projected.status === "read_error") {
        setSuggestMessage({ key: projected.status === "denied" ? "Directory access was denied." : "The directory could not be read safely." });
      }
    } catch {
      /* Without suggestions the path can still be opened as typed. */
    } finally {
      if (suggestWork.current === controller) { suggestWork.current = null; if (alive.current) setSuggestBusy(false); }
    }
  }
  const completable = canImport && allowCompletion && !busy && !importEvidence && !!epoch && typeof folderCompletionRequest(query, epoch) !== "string";
  useEffect(() => {
    if (!completable) return;
    const timer = window.setTimeout(() => void suggest(), 200);
    return () => window.clearTimeout(timer);
  }, [query, completable]);
  function insertSuggestion(path: string) {
    if (!visibleSuggestions || !visibleSuggestions.paths.includes(path) || !maySuggest() || suggestWork.current
      || editRevision.current !== visibleSuggestions.revision || draftNow.current !== query
      || !sameScope(visibleSuggestions.scope) || getCurrentEpoch() !== visibleSuggestions.request.expectedHostEpoch) return;
    // The selected option is removed below. Move its focus back inside the modal before unmounting it;
    // otherwise Escape can target the document body instead of this dialog.
    document.getElementById("saved-project-filter")?.focus();
    // A completed folder ends with its separator, so typing goes on inside it.
    const separator = path.includes("\\") ? "\\" : "/";
    const completed = path.endsWith(separator) ? path : path + separator;
    editRevision.current++;
    draftNow.current = completed;
    setQuery(completed);
    setActive(0);
    clearSuggestions();
    setPreview(undefined);
    setConfirmed(false);
    setMessage("");
    setNotice("");
    setSuggestMessage("");
  }
  useLayoutEffect(() => {
    const list = results.current;
    const option = list?.querySelector<HTMLElement>('[role="option"][aria-selected="true"]');
    if (!list || !option) return;
    const viewport = list.getBoundingClientRect();
    const selected = option.getBoundingClientRect();
    if (selected.top < viewport.top) list.scrollTop += selected.top - viewport.top;
    else if (selected.bottom > viewport.bottom) list.scrollTop += selected.bottom - viewport.bottom;
  });
  function choose(project: SavedProjectIdentity) {
    if (!alive.current) return;
    if (busyNow.current || busy || refreshFailed || opening.getSnapshot() || !snapshot?.configured
      || getCurrentSnapshot() !== snapshot || !savedProjectSelection(project, getCurrentSnapshot())) {
      setMessage({ key: "The saved project is no longer verified in this list. Refresh and select its current entry." });
      return;
    }
    if (!onOpen(project)) setMessage({ key: "The saved project changed before navigation. Refresh and select its current entry." });
  }
  // Opens what the field names: the selected saved project, the selected folder (completed into the field
  // unless it is the typed path itself), or the typed path as a folder to add.
  function openFolder(path: string | undefined) {
    if (path && path !== typedFolder) insertSuggestion(path);
    else if (canImport && typedFolder) void checkPath();
  }
  function submit() {
    if (index >= 0 && index < matches.length) choose(matches[index]);
    else openFolder(activeFolder);
  }
  function savedKeys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      if (itemCount) setActive((index + (event.key === "ArrowDown" ? 1 : itemCount - 1)) % itemCount);
    } else if (event.key === "Tab" && !event.shiftKey && activeFolder && activeFolder !== typedFolder) {
      event.preventDefault(); insertSuggestion(activeFolder);
    } else if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault(); submit();
    }
  }
  // The folder chosen before the window opened is checked once, like a typed path submitted with Enter.
  useEffect(() => { if (initialFolder?.trim()) void checkPath(initialFolder); }, []);
  // Puts a folder into the field as if it had been typed, and checks it. A folder that is a saved project is opened.
  function takeFolder(path: string) {
    const saved = snapshot?.projects.find(project => sameFolder(project.path, path));
    if (saved) { choose(saved); return; }
    editRevision.current++;
    draftNow.current = path;
    setQuery(path);
    setActive(0);
    void checkPath(path);
  }
  // The operating system's folder dialog, opened in the folder the field names when it names one.
  async function browse() {
    if (!pickFolder || !canImport || picking.current || busyNow.current || busy || opening.getSnapshot()) return;
    picking.current = true;
    const pick = await pickFolder(typedFolder || null);
    picking.current = false;
    if (!alive.current) return;
    if (pick.status === "ok") { takeFolder(pick.path); return; }
    if (pick.status === "unavailable" || pick.status === "failed") setMessage({ key: pick.status === "unavailable"
      ? "This system has no folder dialog. Type the folder path instead." : "The folder dialog could not be opened. Type the folder path instead." });
    document.getElementById("saved-project-filter")?.focus();
  }
  async function checkPath(path?: string) {
    // A suggestion request for the text before a chosen folder is dropped: the folder replaces what was typed.
    if (path !== undefined) clearSuggestions();
    if (busyNow.current || busy || suggestWork.current || opening.getSnapshot() || !canImport) return;
    clearSuggestions();
    const requested = (path ?? query).trim();
    busyNow.current = true;
    setBusy(true);
    setPreview(undefined);
    setConfirmed(false);
    setMessage("");
    setNotice("");
    const result = await opening.preview(epoch, requested, capability);
    if (!alive.current) return;
    busyNow.current = false;
    setBusy(false);
    if (result.kind === "ready") setPreview(result);
    else setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "invalid_response"));
  }
  async function importPath() {
    // The button states the trust in the folder; pressing it is the confirmation.
    setConfirmed(true);
    if (busyNow.current || suggestWork.current || opening.getSnapshot() || !canImportCheckedFolder(preview, true, busy, canImport) || !preview) return;
    clearSuggestions();
    busyNow.current = true;
    setBusy(true);
    setMessage("");
    setNotice("");
    const result = await opening.import(epoch, preview, capability);
    if (!alive.current) return;
    busyNow.current = false;
    if (result.kind === "imported") {
      if (await onImported(result.id, result.path, followUp.current.signal)) {
        if (alive.current && opening.confirmOpened(epoch!, result.path, result.id)) close();
        return;
      }
      if (!alive.current) return;
      setMessage({ key: "The project may have been imported, but the refreshed catalog did not show it. Inspect the project list; this captured request cannot be retried in this window." });
    } else setMessage(projectOpeningMessage(result.kind === "error" ? result.code : "import_unconfirmed"));
    setPreview(undefined);
    setConfirmed(false);
    setBusy(false);
  }
  async function refreshList() {
    if (busyNow.current || busy) return;
    clearSuggestions();
    busyNow.current = true;
    setBusy(true);
    const fresh = await onRefresh(followUp.current.signal);
    if (!alive.current) return;
    busyNow.current = false;
    setBusy(false);
    setRefreshFailed(!fresh);
    if (fresh) { if (!opening.getSnapshot()) setMessage(""); setNotice({ key: opening.getSnapshot()
      ? "Project list refreshed for inspection. The captured import remains locked; no request was retried."
      : "Project list refreshed. Check the entries before requesting another import." }); }
    else if (opening.getSnapshot()) setNotice({ key: "Could not refresh the project list. The captured import remains retained." });
    else setMessage({ key: "Could not refresh the project list. No import was requested." });
  }
  const locked = busy || refreshFailed || !!importEvidence;
  const canBrowse = !!pickFolder && canImport;
  return <AppWindow storageKey="codealta.desktop.window.open-project.v1" className="open-project-dialog" titleId="open-project-title"
    title={<><AppIcon name="open" size={14} /> {t("Open project")}</>}
    preferredSize={viewport => ({ width: Math.min(780, viewport.width - 40), height: Math.min(620, viewport.height - 40) })} minimumSize={{ width: 440, height: 340 }}
    onClose={dismiss} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); dismiss(); }}
    // Escape closes the window even from the search field, where it would otherwise only clear the text.
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); dismiss(); }
      // Ctrl+O opened this window; pressed again here, it opens the operating system's folder dialog.
      else if (canBrowse && event.key.toLowerCase() === "o" && event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey && !event.repeat) {
        event.preventDefault(); void browse();
      }
    }}
    onOpened={() => document.getElementById("saved-project-filter")?.focus()}
    headerActions={<><span className="reference-status" role="status">{(busy || suggestBusy) && <ActivitySpinner size={12} />}{matches.length === 1 ? t("1 project") : t("{count} projects", { count: matches.length })}</span>
      <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} disabled={busy} aria-label={t("Refresh projects")} title={t("Refresh projects")} onClick={() => void refreshList()} /></>}>
    <InputGroup id="saved-project-filter" className="reference-search" type="search" role="combobox" aria-autocomplete="list" spellCheck={false}
      leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} aria-label={t("Project name or folder path")}
      aria-expanded={itemCount > 0} aria-controls="saved-project-results" disabled={!!importEvidence}
      aria-activedescendant={index >= 0 ? `saved-project-${index}` : undefined} value={query}
      onChange={event => { editRevision.current++; draftNow.current = event.target.value; setQuery(event.target.value); setActive(0);
        clearSuggestions(); setPreview(undefined); setConfirmed(false); setMessage(""); setNotice(""); }}
      onKeyDown={savedKeys} placeholder={t("Project name or folder path")}
      rightElement={canBrowse ? <Button variant="minimal" size="small" className="open-project-browse" icon={<AppIcon name="folderSearch" size={15} />}
        disabled={busy || !!importEvidence} aria-label={`${t("Browse for a folder")} (Ctrl+O)`} title={`${t("Browse for a folder")} (Ctrl+O)`}
        onClick={() => void browse()} /> : undefined} />
    <div ref={results} id="saved-project-results" className="reference-list" role="listbox" aria-label={t("Saved projects and folders")}>
      {matches.map((project, i) => <div role="option" id={`saved-project-${i}`} key={`${project.id}:${project.path}:${i}`} className="reference-row"
        aria-selected={i === index} aria-disabled={locked} title={project.path}
        onMouseMove={() => { if (i !== index) setActive(i); }} onClick={() => { if (!locked) choose(project); }}>
        <span className="reference-icon" data-file-tone={project.archived ? "muted" : "gold"}><AppIcon name="folder" size={16} /></span>
        <span className="reference-name">{project.name}</span>
        {project.archived ? <Tag minimal>{t("Archived")}</Tag> : <span />}
        <span className="reference-parent">{project.path}</span>
      </div>)}
      {folders().map((path, i) => { const row = matches.length + i; const cut = Math.max(path.lastIndexOf("\\"), path.lastIndexOf("/"));
        return <div role="option" id={`saved-project-${row}`} key={`folder:${path}`} className="reference-row" aria-selected={row === index} title={path}
          onMouseMove={() => { if (row !== index) setActive(row); }} onClick={() => openFolder(path)}>
          <span className="reference-icon" data-file-tone="muted"><AppIcon name="open" size={16} /></span>
          <span className="reference-name">{path.slice(cut + 1)}</span>
          <Tag minimal intent={path === typedFolder ? "primary" : "none"}>{t(path === typedFolder ? "Open this folder" : "Folder")}</Tag>
          <span className="reference-parent">{path.slice(0, cut + 1)}</span>
        </div>; })}
      {itemCount === 0 && <p className="reference-empty" role="status">{t(!query.trim() ? "No saved projects in this snapshot."
        : canImport ? "No saved project or folder matches. Press Enter to open the typed folder." : "No saved projects match this name or path.")}</p>}
    </div>
    {!snapshot && <p role="alert" className="open-project-notice error-text">{t("The saved project list is unavailable. Refresh projects before selecting.")}</p>}
    {refreshFailed && <p role="alert" className="open-project-notice error-text">{t("Saved selection is paused after a failed refresh. Retry Refresh projects before selecting.")}</p>}
    {importEvidence && <p className="open-project-notice" role={importEvidence.kind === "pending" ? "status" : "alert"}>
      {t("Captured import {state} for host", { state: importEvidence.kind })} <code>{importEvidence.epoch}</code>, {t("requested")} <code>{importEvidence.requestedPath}</code>,
      {t("verified folder")} <code>{importEvidence.path}</code>{importEvidence.projectId && <>, {t("Project ID")} <code>{importEvidence.projectId}</code></>}.
    </p>}
    {suggestMessage && <p role="status" id="folder-suggestion-status" className="open-project-notice bp6-text-muted">{workflowNotice(locale, suggestMessage)}</p>}
    {canImport && preview && <div className="project-import-row project-import-confirm">
      <AppIcon name="open" size={15} /><code title={preview.path}>{preview.path}</code>
      <Button intent="primary" autoFocus disabled={!!importEvidence || busy} onClick={() => void importPath()}>{t("Trust and open folder")}</Button>
    </div>}
    {notice && <p role="status" className="open-project-notice">{workflowNotice(locale, notice)}</p>}
    {message && <p role="alert" className="open-project-notice error-text">{workflowNotice(locale, message)}</p>}
    <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("open")}</span><span><kbd>Tab</kbd> {t("complete")}</span>{canBrowse && <span><kbd>Ctrl+O</kbd> {t("browse")}</span>}<span><kbd>Esc</kbd> {t("close")}</span></footer>
  </AppWindow>;
}
