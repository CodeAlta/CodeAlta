import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { boot, workspace, type BootStatus } from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import "./style.css";

function App() {
  const [status, setStatus] = useState<BootStatus>();
  const [error, setError] = useState<string>();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const [projectId, setProjectId] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => { if (!abort.signal.aborted) setStatus(value); })
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
    <p>Browse persisted workspace metadata from a trusted task-owned <strong>COPY</strong>. This is not live session state: sending, resuming, events and history are not connected.</p>
    <p>Use <code>altatui</code> for current agent functionality.</p>
    <p role="status">{error ?? (status ? `Desktop bridge ready · ${status.version}` : "Initializing desktop bridge…")}</p>
    <p className="detail">Browser storage uses the separate new <code>--data-root</code>. Catalog browsing requires <code>--catalog-root</code> and <code>--allow-catalog-cache</code>: the shared loader may write <code>cache/cache.sqlite3</code> and SQLite sidecars in that copy. No default or production profile is opened. Path spelling does not prove ownership or isolate reparse points.</p>
    <p className="detail">The shared session catalog retains its snapshot; this screen does not refresh or invalidate it. Close and relaunch to reload. Response limits do not bound the underlying scan. Canceling a request or closing this window does not guarantee stopping the catalog/cache load.</p>

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
        </section>}
      </>}
    </section>
  </main>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
