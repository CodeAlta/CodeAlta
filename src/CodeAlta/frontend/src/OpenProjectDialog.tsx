import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type KeyboardEvent as ReactKeyboardEvent } from "react";
import type { WorkspaceDirectoryCompletionRequest, WorkspaceDirectoryCompletionResponse, WorkspaceSnapshot } from "#neoastra";
import type { createMutationCapability } from "./sessionOperations";
import { canImportCheckedFolder, projectOpeningMessage, type createProjectOpening } from "./projectOpening";
import { savedProjectSelection, type SavedProjectIdentity } from "./savedProjectSelection";
import { AppIcon } from "./AppIcon";
import { folderCompletionRequest, projectFolderCompletion } from "./directoryCompletion";

export function OpenProjectDialog({ snapshot, getCurrentSnapshot, epoch, getCurrentEpoch, getCurrentScope, allowCompletion, capability, opening,
  completeDirectory, onOpen, onRefresh, onImported, onClose }: {
  snapshot: WorkspaceSnapshot | undefined; getCurrentSnapshot: () => WorkspaceSnapshot | undefined; epoch: string | undefined;
  getCurrentEpoch: () => string | undefined;
  getCurrentScope: () => { projectId: string | null; sessionId: string | null };
  allowCompletion: boolean;
  capability: ReturnType<typeof createMutationCapability> | undefined;
  opening: ReturnType<typeof createProjectOpening>;
  completeDirectory: (request: WorkspaceDirectoryCompletionRequest, options: { signal: AbortSignal }) => Promise<WorkspaceDirectoryCompletionResponse>;
  onOpen: (project: SavedProjectIdentity) => boolean; onRefresh: (signal: AbortSignal) => Promise<{ configured: boolean } | undefined>;
  onImported: (id: string, path: string, signal: AbortSignal) => Promise<boolean>;
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState("");
  const [active, setActive] = useState(0);
  const [preview, setPreview] = useState<{ requestedPath: string; path: string }>();
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [notice, setNotice] = useState("");
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [suggestBusy, setSuggestBusy] = useState(false);
  const [suggestMessage, setSuggestMessage] = useState("");
  const [suggestions, setSuggestions] = useState<{ request: WorkspaceDirectoryCompletionRequest; paths: string[];
    revision: number; scope: { projectId: string | null; sessionId: string | null } }>();
  const [suggestActive, setSuggestActive] = useState(0);
  const [, notifyCapability] = useState(0);
  const importEvidence = useSyncExternalStore(opening.subscribe, opening.getSnapshot);
  const canImport = !!epoch && !!capability?.canMutate();
  const alive = useRef(true);
  const origin = useRef(document.activeElement instanceof HTMLElement ? document.activeElement : null);
  const busyNow = useRef(false);
  const draftNow = useRef("");
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
    if (origin.current?.isConnected && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.current.focus();
  });
  const close = () => {
    alive.current = false; followUp.current.abort(); suggestWork.current?.abort(); suggestWork.current = null; onClose();
    restoreFocus();
  };
  const normalized = filter.trim().toLowerCase();
  const matches = (snapshot?.projects ?? []).filter(project => !normalized || project.name.toLowerCase().includes(normalized)
    || project.path.toLowerCase().includes(normalized))
    .sort((a, b) => a.name.toLowerCase() < b.name.toLowerCase() ? -1 : a.name.toLowerCase() > b.name.toLowerCase() ? 1
      : a.id < b.id ? -1 : a.id > b.id ? 1 : a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  const index = Math.min(active, matches.length - 1);
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
  const scopeAtRender = getCurrentScope();
  const completionAuthority = !!epoch && getCurrentEpoch() === epoch && !!capability?.canMutate();
  useEffect(() => {
    // A host or selected target transition invalidates even an ABA return to the old identity.
    editRevision.current++;
    clearSuggestions();
  }, [epoch, scopeAtRender.projectId, scopeAtRender.sessionId, allowCompletion, completionAuthority, !!importEvidence]);
  async function suggest() {
    if (!maySuggest() || suggestWork.current) return;
    const request = folderCompletionRequest(draftNow.current, epoch!);
    clearSuggestions();
    if (typeof request === "string") { setSuggestMessage(request); return; }
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
      if (!projected) { setSuggestMessage("Folder suggestions returned an invalid or foreign response. Request again explicitly if needed."); return; }
      if (projected.status === "complete" || projected.status === "incomplete") {
        setSuggestions({ request, paths: projected.directories, revision, scope });
        setSuggestMessage(`${projected.directories.length ? `Observed ${projected.directories.length} matching folders` : "No matching folders observed"}
          (${projected.entriesVisited} entries inspected). ${projected.status === "incomplete" ? "Incomplete observed subset; other folders may exist. " : "Enumeration completed. "}
          ${projected.omittedUnsafeEntries ? "Unsafe entries were omitted. " : ""}Suggestions do not establish ownership or importability.`);
      } else setSuggestMessage({ invalid_request: "The folder/prefix is not canonical or supported by the host.",
        missing: "The directory is missing.", not_directory: "The path is not a directory.", denied: "Directory access was denied.",
        read_error: "The directory could not be read safely.", unconfigured: "Folder suggestions are unavailable on this host.",
        stale_epoch: "The host changed; this suggestion request is stale.", closed: "The host is closing.",
        busy: "A previous directory read is still in progress. A new explicit request is required." }[projected.status]
        ?? "Unknown folder suggestion outcome. Request again explicitly if needed.");
    } catch {
      if (current()) setSuggestMessage("The folder suggestion wait failed or was canceled; request again explicitly if needed. The host read may still be running.");
    } finally {
      if (suggestWork.current === controller) { suggestWork.current = null; if (alive.current) setSuggestBusy(false); }
    }
  }
  function insertSuggestion(path: string) {
    if (!visibleSuggestions || !visibleSuggestions.paths.includes(path) || !maySuggest() || suggestWork.current
      || editRevision.current !== visibleSuggestions.revision || draftNow.current !== query
      || !sameScope(visibleSuggestions.scope) || getCurrentEpoch() !== visibleSuggestions.request.expectedHostEpoch) return;
    editRevision.current++;
    draftNow.current = path;
    setQuery(path);
    clearSuggestions();
    setPreview(undefined);
    setConfirmed(false);
    setMessage("");
    setNotice("");
    setSuggestMessage("Folder inserted into the draft. Check folder and explicitly confirm before importing.");
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
      setMessage("The saved project is no longer verified in this list. Refresh and select its current entry.");
      return;
    }
    if (onOpen(project)) restoreFocus();
    else setMessage("The saved project changed before navigation. Refresh and select its current entry.");
  }
  function savedKeys(event: ReactKeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
    if ((event.key === "ArrowDown" || event.key === "ArrowUp") && !event.ctrlKey && !event.altKey && !event.metaKey) {
      event.preventDefault();
      if (matches.length) setActive((index + (event.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length);
    } else if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && index >= 0) {
      event.preventDefault(); choose(matches[index]);
    }
  }
  async function checkPath() {
    if (busyNow.current || busy || suggestWork.current || opening.getSnapshot() || !canImport) return;
    clearSuggestions();
    const requested = query.trim();
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
    if (busyNow.current || suggestWork.current || opening.getSnapshot() || !canImportCheckedFolder(preview, confirmed, busy, canImport) || !preview) return;
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
      setMessage("The project may have been imported, but the refreshed catalog did not show it. Inspect the project list; this captured request cannot be retried in this window.");
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
    if (fresh) { if (!opening.getSnapshot()) setMessage(""); setNotice(opening.getSnapshot()
      ? "Project list refreshed for inspection. The captured import remains locked; no request was retried."
      : "Project list refreshed. Check the entries before requesting another import."); }
    else if (opening.getSnapshot()) setNotice("Could not refresh the project list. The captured import remains retained.");
    else setMessage("Could not refresh the project list. No import was requested.");
  }
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close(); }}>
    <section className="app-dialog" role="dialog" aria-modal="true" aria-labelledby="open-project-title"
      onKeyDown={event => {
        event.stopPropagation();
        if (event.defaultPrevented || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
        if (event.key === "Escape") { event.preventDefault(); close(); }
        else if (event.key === "Tab") {
          const controls = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled])'));
          if (event.shiftKey && document.activeElement === controls[0]) { event.preventDefault(); controls.at(-1)?.focus(); }
          else if (!event.shiftKey && document.activeElement === controls.at(-1)) { event.preventDefault(); controls[0]?.focus(); }
        }
      }}>
      <header><div><span className="eyebrow">Workspace</span><h2 id="open-project-title">Open project</h2></div><button type="button" className="icon-button" aria-label="Close" title="Close" onClick={close}><AppIcon name="close" size={16} /></button></header>
      <label htmlFor="saved-project-filter">Find a saved project by name or full path</label>
      <input autoFocus id="saved-project-filter" type="search" role="combobox" aria-autocomplete="list"
        aria-expanded={matches.length > 0} aria-controls="saved-project-results"
        aria-activedescendant={index >= 0 ? `saved-project-${index}` : undefined} value={filter}
        onChange={event => { setFilter(event.target.value); setActive(0); }} onKeyDown={savedKeys} placeholder="Saved project name or path" />
      <div ref={results} id="saved-project-results" className="dialog-list" role="listbox" aria-label="Saved projects (name order)">
        {matches.map((project, i) => <button type="button" role="option" id={`saved-project-${i}`} key={`${project.id}:${project.path}:${i}`}
          aria-selected={i === index} onFocus={() => setActive(i)} onMouseEnter={() => setActive(i)} onClick={() => choose(project)}
          onKeyDown={event => { if (event.key === "Enter" && !event.repeat && !event.defaultPrevented && !event.nativeEvent.isComposing
            && event.nativeEvent.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) {
            event.preventDefault(); choose(project); } }}
          disabled={busy || refreshFailed || !!importEvidence}>
          <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}{project.archived ? " (archived, read-only)" : ""}</strong><small title={project.path}>{project.path}</small></span>
        </button>)}
        {matches.length === 0 && <p role="status">{filter.trim() ? "No saved projects match this name or path." : "No saved projects in this snapshot."}</p>}
      </div>
      {snapshot?.projectsTruncated && <p role="status" className="muted-text">Saved project snapshot is truncated; omitted projects cannot be searched here. Refresh to inspect the current bounded list.</p>}
      {!snapshot && <p role="alert">The saved project list is unavailable. Refresh projects before selecting.</p>}
      {refreshFailed && <p role="alert">Saved selection is paused after a failed refresh. Retry Refresh projects before selecting.</p>}
      {importEvidence && <p role={importEvidence.kind === "pending" ? "status" : "alert"}>
        Captured import {importEvidence.kind} for host <code>{importEvidence.epoch}</code>, requested <code>{importEvidence.requestedPath}</code>,
        verified folder <code>{importEvidence.path}</code>{importEvidence.projectId && <>, project ID <code>{importEvidence.projectId}</code></>}.
        {importEvidence.kind === "pending" ? " Wait for the original request. No saved selection or new folder request is available."
          : " Inspect the catalog; no navigation, new request or retry will run in this window. Reload the app only after resolving this exact outcome."}
      </p>}
      {!canImport && <p className="muted-text">Adding a folder requires an owned host. Catalog-only browsing never changes the project list.</p>}
      {canImport && <div className="project-import">
        <label htmlFor="project-folder-path">Add a different existing folder (requires trust confirmation)</label>
        <input id="project-folder-path" aria-label="Absolute folder path to check" value={query} disabled={busy || !!importEvidence}
          onChange={event => { editRevision.current++; draftNow.current = event.target.value; setQuery(event.target.value);
            clearSuggestions(); setPreview(undefined); setConfirmed(false); setMessage(""); setNotice(""); }}
          onKeyDown={event => { if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing
            || event.nativeEvent.keyCode === 229 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
            if (event.key === "ArrowDown" && visibleSuggestions?.paths.length) {
              event.preventDefault(); document.querySelector<HTMLElement>('#folder-suggestions [role="option"]')?.focus();
            } else if (event.key === "Enter") { event.preventDefault(); void checkPath(); } }} placeholder="Absolute folder path" />
        <button type="button" className="quiet-button" disabled={busy || suggestBusy || !!importEvidence || !query.trim()} onClick={() => void checkPath()}>Check folder</button>
        <button type="button" className="quiet-button" disabled={busy || suggestBusy || !!importEvidence || !query || !canImport || !allowCompletion || getCurrentEpoch() !== epoch}
          onClick={() => void suggest()}>Suggest folders</button>
        {suggestBusy && <p role="status">Reading a bounded folder subset… Canceling this wait does not stop a blocking host read.</p>}
        {suggestMessage && <p role="status" id="folder-suggestion-status">{suggestMessage}</p>}
        {visibleSuggestions && visibleSuggestions.paths.length > 0 && <div id="folder-suggestions" className="dialog-list" role="listbox" aria-label="Observed folder suggestions">
          {visibleSuggestions.paths.map((path, i) => <button type="button" role="option" key={path} aria-selected={i === suggestActive}
            onFocus={() => setSuggestActive(i)} onMouseEnter={() => setSuggestActive(i)}
            onClick={event => { if (!event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey) insertSuggestion(path); }}
            onKeyDown={event => {
              if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229
                || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
                if (event.key === "Enter" || event.key === " ") event.preventDefault();
                return;
              }
              if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault(); const next = (i + (event.key === "ArrowDown" ? 1 : visibleSuggestions.paths.length - 1)) % visibleSuggestions.paths.length;
                setSuggestActive(next); event.currentTarget.parentElement?.querySelectorAll<HTMLElement>('[role="option"]')[next]?.focus();
              } else if (event.key === "Enter") { event.preventDefault(); insertSuggestion(path); }
            }}><small title={path}>{path}</small></button>)}
        </div>}
        {preview && <><p>Existing folder: <code>{preview.path}</code></p>
          <label><input type="checkbox" checked={confirmed} disabled={busy || !!importEvidence} onChange={event => setConfirmed(event.target.checked)} /> I trust this folder and want to add it to the active project catalog.</label>
          <button type="button" className="quiet-button" disabled={!!importEvidence || !canImportCheckedFolder(preview, confirmed, busy, canImport)} onClick={() => void importPath()}>Import and open folder</button></>}
      </div>}
      {busy && <p role="status">Checking or importing the folder…</p>}
      {notice && <p role="status">{notice}</p>}
      {message && <p role="alert" className="error-text">{message}</p>}
      <footer><span><kbd>Ctrl</kbd>+<kbd>O</kbd> · <kbd>Esc</kbd></span><span>
        <button type="button" className="quiet-button" disabled={busy} onClick={() => void refreshList()}>Refresh projects</button>{" "}
        <button type="button" className="quiet-button" onClick={close}>Cancel</button></span></footer>
    </section>
  </div>;
}
