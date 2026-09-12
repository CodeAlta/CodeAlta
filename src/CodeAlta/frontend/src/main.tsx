import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { boot, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations, type BootStatus } from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { loadHistory, historyMessage, type HistoryState } from "./history";
import type { HistoryRequest } from "#neoastra";
import type { SessionSendRequest } from "#neoastra";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { createMutationCapability } from "./sessionOperations";
import { createSessionDisplayStore } from "./sessionDisplay";
import { createRuntimeStateReader } from "./runtimeState";
import { createPermissionReviewer } from "./sessionPermissions";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import "./style.css";

function App() {
  const [status, setStatus] = useState<BootStatus>();
  const [error, setError] = useState<string>();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const [projectId, setProjectId] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [submissions] = useState(() => new Map<string, SessionSendRequest>());
  const [steering] = useState(() => createSteeringSubmissions(sessionOperations.steer));
  const [compaction] = useState(() => createCompactionSubmissions(sessionOperations.compact));
  const [abortRuns] = useState(() => createAbortRunSubmissions(sessionOperations.abortRun));
  const [queue] = useState(() => createQueueSubmissions(sessionOperations.queue, sessionOperations.cancelQueue));
  const [display] = useState(() => createSessionDisplayStore(sessionDisplay.observe));
  const [runtimeReader] = useState(() => createRuntimeStateReader(sessionRuntimeState.current));
  const [permissionReviewer] = useState(() => createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve));
  const [mutation, setMutation] = useState<{ epoch: string; capability: ReturnType<typeof createMutationCapability> }>();
  useEffect(() => {
    const abort = new AbortController();
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => {
        if (abort.signal.aborted) return;
        setStatus(value);
        setMutation(current => current?.epoch === value.hostEpoch ? current
          : value.hostEpoch ? { epoch: value.hostEpoch, capability: createMutationCapability(value.hostEpoch) } : undefined);
      })
      .catch(() => { if (!abort.signal.aborted) setError("The desktop bridge could not be initialized. Close the window and try again."); });
    void loadWorkspace(workspace.snapshot, abort.signal, setWorkspaceState);
    return () => abort.abort();
  }, []);

  const snapshot = workspaceState.kind === "ready" ? workspaceState.snapshot : undefined;
  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  const selectedSession = sessions.find(value => value.id === sessionId);
  const selectedProject = snapshot?.projects.find(value => value.id === projectId);
  const notice = snapshot ? workspaceNotice(snapshot) : null;

  return <main>
    <h1>CodeAlta</h1>
    <p className="badge">Desktop — in development</p>
    {status?.hostAvailable
      ? <p>Explicit owned-host mode: text submission, receipts and selected-session live status/text for existing sessions. The live window is partial; a submission receipt is not run completion. Configured providers may use authentication/storage/network; discovery roots are not a sandbox.</p>
      : <p>Browse persisted workspace metadata from a trusted task-owned <strong>COPY</strong>. Read-only persisted history is available on selection. This is not live session state: sending, resuming and live events are not connected.</p>}
    <p>Use <code>altatui</code> for current agent functionality.</p>
    <p role="status">{error ?? (status ? `Desktop bridge ready · ${status.version}` : "Initializing desktop bridge…")}</p>
    <p className="detail">Browser storage uses the separate new <code>--data-root</code>. Catalog browsing requires <code>--catalog-root</code> and <code>--allow-catalog-cache</code>: the shared loader may write <code>cache/cache.sqlite3</code> and SQLite sidecars in that copy. No default or production profile is opened. Path spelling does not prove ownership or isolate reparse points.</p>
    {status?.hostAvailable
      ? <p className="detail">Owned mode reads the host-shared cached store directly, with eight actual reads maximum. Full scans are not bounded by display limits. Shutdown joins actual reads and commands before dependencies; pending or unconfirmed cleanup keeps the lease.</p>
      : <p className="detail">The shared session catalog retains its snapshot; this screen does not refresh or invalidate it. Close and relaunch to reload. Response limits do not bound the underlying scan. Canceling a request or closing this window does not guarantee stopping the catalog/cache load.</p>}

    <section aria-labelledby="workspace-heading">
      <h2 id="workspace-heading">Workspace snapshot</h2>
      {workspaceState.kind === "loading" && <p role="status">Loading persisted projects and sessions…</p>}
      {workspaceState.kind === "unconfigured" && <p role="status">No catalog copy configured. Relaunch with a new browser data root, an existing absolute trusted task-owned COPY via <code>--catalog-root</code>, and <code>--allow-catalog-cache</code>.</p>}
      {workspaceState.kind === "error" && <p role="alert">{workspaceState.message}</p>}
      {snapshot && <>
        {notice && <p role="status" className="notice">{notice}</p>}
        {snapshot.projects.length === 0 && snapshot.sessions.length === 0 && <p role="status">The supplied catalog contains no projects or persisted sessions.</p>}
        <div className="workspace-grid">
          <nav aria-label="Project groups">
            <h3>Projects</h3>
            {snapshot.projects.length === 0 && <p>No projects in this snapshot.</p>}
            <ul className="choices">
              <li><button type="button" aria-pressed={projectId === null} onClick={() => { setProjectId(null); setSessionId(null); }}>Global / unmatched</button></li>
              {snapshot.projects.map(project => <li key={project.id}><button type="button" aria-pressed={projectId === project.id} onClick={() => { setProjectId(project.id); setSessionId(null); }}>
                {project.name}{project.archived ? " (archived)" : ""}
              </button></li>)}
            </ul>
          </nav>
          <section aria-labelledby="sessions-heading">
            <h3 id="sessions-heading">{selectedProject?.name ?? "Global / unmatched"} — persisted sessions</h3>
            {selectedProject && <p className="path">{selectedProject.path}</p>}
            <p className="detail">Grouping uses exact persisted workspace paths, not active project ownership.</p>
            {sessions.length === 0 && <p role="status">No persisted sessions in this group.</p>}
            <ul className="choices">
              {sessions.map(session => <li key={session.id}><button type="button" aria-pressed={sessionId === session.id} onClick={() => setSessionId(session.id)}>
                {session.title} <span className="detail">· {session.updatedAt}</span>
              </button></li>)}
            </ul>
          </section>
        </div>
        {selectedSession && <section aria-labelledby="session-details-heading" className="session-details">
          <h3 id="session-details-heading">Persisted session details</h3>
          <dl>
            <dt>Title / summary</dt><dd>{selectedSession.title}</dd>
            <dt>Session ID</dt><dd>{selectedSession.id}</dd>
            <dt>Workspace path</dt><dd>{selectedSession.workspacePath || "Not recorded"}</dd>
            <dt>Configured provider key</dt><dd>{selectedSession.providerKey || "Not recorded"}</dd>
            <dt>Persisted update time</dt><dd>{selectedSession.updatedAt}</dd>
          </dl>
          {status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch
            ? <OwnedSessionPanel key={JSON.stringify([selectedSession.id, status.hostEpoch])} sessionId={selectedSession.id} epoch={status.hostEpoch} drafts={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} display={display} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} />
            : <History key={selectedSession.id} sessionId={selectedSession.id} />}
        </section>}
      </>}
    </section>
  </main>;
}

function History({ sessionId }: { sessionId: string }) {
  const [request, setRequest] = useState<HistoryRequest>({ sessionId, cursor: null });
  const [state, setState] = useState<HistoryState>();
  useEffect(() => {
    const abort = new AbortController();
    void loadHistory(workspace.history, request, abort.signal, setState);
    return () => abort.abort();
  }, [request]);
  const current = state?.request === request ? state : undefined;
  const page = current?.kind === "ready" ? current.page : undefined;
  return <section aria-labelledby="history-heading">
    <h3 id="history-heading">Persisted event history</h3>
    <p className="detail">One bounded page in journal order, not a reconstructed conversation. Deltas and completed content remain separate records. UTF-8 LF/CRLF only; payloads may be omitted or previews shortened. This does not refresh the catalog.</p>
    <p className="detail">Length/time checks detect changes, not same-stamp rewrites or all external-writer races. Canceling history forwards cancellation but does not guarantee stopping catalog loads or joining work on window close.</p>
    {(!current || current.kind === "loading") && <p role="status">Loading persisted history…</p>}
    {current?.kind === "error" && <p role="alert">{historyMessage(current.code)}</p>}
    {page && <>
      {page.entries.length === 0 && <p role="status">No visible events in this page. Metadata and blank records still count toward its read limit.</p>}
      {page.tailOmitted && <p role="status">The malformed final journal record was omitted; this is not complete history.</p>}
      <ol className="history-records">
        {page.entries.map(entry => <li key={entry.offset}>
          <strong>{entry.eventType}{entry.kind ? ` · ${entry.kind}` : ""}{entry.phase ? ` · ${entry.phase}` : ""}</strong>
          <div className="detail">{entry.timestamp} · byte {entry.offset} · provider {entry.providerId} · recorded session {entry.sessionId}{entry.runId ? ` · run ${entry.runId}` : ""}</div>
          {entry.contentId && <div className="detail">Content: {entry.contentId}</div>}
          {entry.activityId && <div className="detail">Activity: {entry.activityId}</div>}
          {entry.parentActivityId && <div className="detail">Parent activity: {entry.parentActivityId}</div>}
          {entry.name && <p>{entry.name}</p>}
          {entry.text !== null && <pre>{entry.text}</pre>}
          {entry.textTruncated && <p className="detail">Display preview shortened.</p>}
          {entry.bodyOmitted && <p className="detail">Additional stored payload omitted. No action is available for this record.</p>}
        </li>)}
      </ol>
    </>}
    <div className="history-controls">
      <button type="button" onClick={() => setRequest({ sessionId, cursor: null })}>Restart history</button>
      {page?.next && <button type="button" onClick={() => setRequest({ sessionId, cursor: page.next })}>Next page</button>}
    </div>
  </section>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
