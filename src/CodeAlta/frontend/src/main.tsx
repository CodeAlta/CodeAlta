import { StrictMode, useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent } from "react";
import { createRoot } from "react-dom/client";
import {
  boot, configuration, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionUserInput, type BootStatus, type HistoryRequest,
  type ConfigurationSnapshot, type WorkspaceSession,
} from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { loadHistory, historyMessage, type HistoryState } from "./history";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSessionDisplayStore } from "./sessionDisplay";
import { createRuntimeStateReader } from "./runtimeState";
import { createPermissionReviewer } from "./sessionPermissions";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { AskPanel } from "./AskPanel";
import { askWireRequest, createAskActions } from "./sessionAsks";
import { NotesPanel } from "./NotesPanel";
import { createNotesReader } from "./sessionNotes";
import { createUserInputReviewer } from "./sessionUserInput";
import { UserInputPanel } from "./UserInputPanel";
import { MarkdownContent } from "./MarkdownContent";
import { LiveSessionPanel } from "./LiveSessionPanel";
import { constrainPaneLayout, defaultPaneLayout, persistPaneLayout, resizePane, restorePaneLayout, type PaneName } from "./paneLayout";
import "./style.css";

const demoMode = import.meta.env.VITE_DEMO_MODE === "true";
type View = "workspace" | "configuration";
type Theme = "dark" | "light";
const paneLayoutStorageKey = "codealta.desktop.panes.v1";

function App() {
  const [status, setStatus] = useState<BootStatus>();
  const [error, setError] = useState<string>();
  const [workspaceState, setWorkspaceState] = useState<WorkspaceState>({ kind: "loading" });
  const [projectId, setProjectId] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [view, setView] = useState<View>("workspace");
  const [search, setSearch] = useState("");
  const [theme, setTheme] = useState<Theme>("dark");
  const [configurationState, setConfigurationState] = useState<{ snapshot?: ConfigurationSnapshot; error?: string }>({});
  const initialSelectionMade = useRef(false);
  const [submissions] = useState(() => createOwnedSubmissions(sessionOperations.send, sessionOperations.abort));
  const [steering] = useState(() => createSteeringSubmissions(sessionOperations.steer));
  const [compaction] = useState(() => createCompactionSubmissions(sessionOperations.compact));
  const [abortRuns] = useState(() => createAbortRunSubmissions(sessionOperations.abortRun));
  const [queue] = useState(() => createQueueSubmissions(sessionOperations.queue, sessionOperations.cancelQueue));
  const [askActions] = useState(() => createAskActions(
    request => sessionAsks.answer(askWireRequest(request), { timeoutMilliseconds: 8000 }),
    request => sessionAsks.cancel(askWireRequest(request), { timeoutMilliseconds: 8000 })));
  const [display] = useState(() => createSessionDisplayStore(sessionDisplay.observe));
  const [runtimeReader] = useState(() => createRuntimeStateReader(sessionRuntimeState.current));
  const [notesReader] = useState(() => createNotesReader(sessionNotes.current));
  const [inputReviewer] = useState(() => createUserInputReviewer(
    request => sessionUserInput.list(request, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.resolve({ ...request, answers: request.answers.map(answer => ({ ...answer })) }, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.cancel(request, { timeoutMilliseconds: 8000 })));
  const [permissionReviewer] = useState(() => createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve));
  const [mutation, setMutation] = useState<{ epoch: string; capability: ReturnType<typeof createMutationCapability> }>();
  const workspaceShell = useRef<HTMLDivElement>(null);
  const [paneLayout, setPaneLayout] = useState(() => restorePaneLayout(() => localStorage.getItem(paneLayoutStorageKey), window.innerWidth));

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);

  useEffect(() => {
    persistPaneLayout(value => localStorage.setItem(paneLayoutStorageKey, value), paneLayout);
  }, [paneLayout]);

  useEffect(() => {
    const constrain = () => setPaneLayout(current => constrainPaneLayout(current, workspaceShell.current?.clientWidth ?? window.innerWidth));
    window.addEventListener("resize", constrain);
    return () => window.removeEventListener("resize", constrain);
  }, []);

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
    void configuration.snapshot({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => { if (!abort.signal.aborted) setConfigurationState({ snapshot: value }); })
      .catch(() => { if (!abort.signal.aborted) setConfigurationState({ error: "Configuration inventory is unavailable." }); });
    return () => abort.abort();
  }, []);

  const snapshot = workspaceState.kind === "ready" ? workspaceState.snapshot : undefined;
  useEffect(() => {
    if (!snapshot || initialSelectionMade.current) return;
    initialSelectionMade.current = true;
    const firstSession = snapshot.sessions[0];
    if (!firstSession) return;
    const project = snapshot.projects.find(value => value.path === firstSession.workspacePath);
    setProjectId(project?.id ?? null);
    setSessionId(firstSession.id);
  }, [snapshot]);

  const sessions = snapshot ? sessionsForProject(snapshot, projectId) : [];
  const visibleSessions = sessions.filter(session => !search || `${session.title} ${session.providerKey ?? ""}`.toLowerCase().includes(search.toLowerCase()));
  const selectedSession = snapshot?.sessions.find(value => value.id === sessionId);
  const selectedProject = snapshot?.projects.find(value => value.id === projectId);
  const notice = snapshot ? workspaceNotice(snapshot) : null;
  const connected = !!status?.hostAvailable;

  function selectProject(nextProjectId: string | null) {
    setProjectId(nextProjectId);
    const nextSessions = snapshot ? sessionsForProject(snapshot, nextProjectId) : [];
    setSessionId(nextSessions[0]?.id ?? null);
    setView("workspace");
  }

  function changePane(pane: PaneName, delta: number) {
    setPaneLayout(current => resizePane(current, pane, delta, workspaceShell.current?.clientWidth ?? window.innerWidth));
  }

  function resetPane(pane: PaneName) {
    setPaneLayout(current => constrainPaneLayout({ ...current, [pane]: defaultPaneLayout[pane] }, workspaceShell.current?.clientWidth ?? window.innerWidth));
  }

  return <div className="app-shell">
    <header className="topbar">
      <div className="brand"><span className="brand-mark">A</span><span>CodeAlta</span><small>{demoMode ? "interactive preview" : "desktop"}</small></div>
      <nav className="topnav" aria-label="Primary navigation">
        <button type="button" aria-current={view === "workspace" ? "page" : undefined} onClick={() => setView("workspace")}>Sessions</button>
        <button type="button" aria-current={view === "configuration" ? "page" : undefined} onClick={() => setView("configuration")}>Configuration</button>
      </nav>
      <div className={`connection ${error ? "connection-error" : connected ? "connection-live" : "connection-readonly"}`}>
        <span className="connection-dot" />
        {error ? "Bridge unavailable" : demoMode ? "Local demo" : connected ? "Runtime connected" : "Catalog only"}
      </div>
    </header>

    {view === "configuration"
      ? <ConfigurationPanel status={status} selectedSession={selectedSession} configurationState={configurationState} theme={theme} setTheme={setTheme} />
      : <div className="workspace-shell" ref={workspaceShell} style={{
          "--project-pane-width": `${paneLayout.projects}px`,
          "--session-pane-width": `${paneLayout.sessions}px`,
        } as CSSProperties}>
        <aside className="project-rail" aria-label="Projects">
          <div className="panel-title"><span>Projects</span><span className="count">{snapshot?.projects.length ?? 0}</span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">No catalog configured. See the launch instructions below.</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && <ul className="nav-list">
            {snapshot.projects.map(project => <li key={project.id}><button type="button" aria-pressed={projectId === project.id} onClick={() => selectProject(project.id)}>
              <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small>{project.archived ? "Archived" : shortPath(project.path)}</small></span>
            </button></li>)}
            <li><button type="button" aria-pressed={projectId === null} onClick={() => selectProject(null)}>
              <span className="project-icon muted">◇</span><span><strong>Other sessions</strong><small>No matching project</small></span>
            </button></li>
          </ul>}
          <div className="rail-footer">
            <button type="button" className="quiet-button" onClick={() => setView("configuration")}>⚙ Settings &amp; extensions</button>
          </div>
        </aside>

        <PaneSplitter label="Resize projects" value={paneLayout.projects} onResize={delta => changePane("projects", delta)} onReset={() => resetPane("projects")} />

        <aside className="session-rail" aria-label="Sessions">
          <div className="session-rail-header">
            <div><span className="eyebrow">Sessions</span><h2>{selectedProject?.name ?? "Other sessions"}</h2></div>
            <button type="button" className="icon-button" title="Refresh by relaunching the current desktop host" disabled>＋</button>
          </div>
          <label className="search"><span>⌕</span><input value={search} onChange={event => setSearch(event.target.value)} placeholder="Search sessions" /></label>
          {notice && <p role="status" className="notice">{notice}</p>}
          <div className="session-list">
            {visibleSessions.map(session => <button type="button" key={session.id} aria-pressed={sessionId === session.id} onClick={() => setSessionId(session.id)}>
              <span className="session-title">{session.title}</span>
              <span className="session-meta"><span>{session.providerKey ?? "No provider"}</span><time>{session.updatedAt}</time></span>
            </button>)}
            {snapshot && visibleSessions.length === 0 && <div className="sidebar-empty">{search ? "No matching sessions." : "No sessions in this project."}</div>}
          </div>
        </aside>

        <PaneSplitter label="Resize sessions" value={paneLayout.sessions} onResize={delta => changePane("sessions", delta)} onReset={() => resetPane("sessions")} />

        <main className="content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {!selectedSession
            ? <EmptyWorkspace workspaceState={workspaceState} />
            : <SessionWorkspace key={selectedSession.id} session={selectedSession} status={status} mutation={mutation}
                submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
                askActions={askActions} display={display} runtimeReader={runtimeReader} notesReader={notesReader}
                permissionReviewer={permissionReviewer} inputReviewer={inputReviewer} />}
        </main>
      </div>}
  </div>;
}

function SessionWorkspace({ session, status, mutation, submissions, steering, compaction, abortRuns, queue, askActions, display, runtimeReader, notesReader, permissionReviewer, inputReviewer }: {
  session: WorkspaceSession;
  status: BootStatus | undefined;
  mutation: { epoch: string; capability: ReturnType<typeof createMutationCapability> } | undefined;
  submissions: ReturnType<typeof createOwnedSubmissions>;
  steering: ReturnType<typeof createSteeringSubmissions>;
  compaction: ReturnType<typeof createCompactionSubmissions>;
  abortRuns: ReturnType<typeof createAbortRunSubmissions>;
  queue: ReturnType<typeof createQueueSubmissions>;
  askActions: ReturnType<typeof createAskActions>;
  display: ReturnType<typeof createSessionDisplayStore>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  notesReader: ReturnType<typeof createNotesReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer>;
  inputReviewer: ReturnType<typeof createUserInputReviewer>;
}) {
  return <div className="session-workspace">
    <header className="session-header">
      <div><span className="eyebrow">Session</span><h1>{session.title}</h1></div>
      <div className="session-chips"><span>{session.providerKey ?? "Provider not recorded"}</span><span>{status?.hostAvailable ? "Live" : "Persisted"}</span></div>
    </header>
    <details className="session-info"><summary>Session details</summary><dl>
      <dt>ID</dt><dd>{session.id}</dd><dt>Project</dt><dd>{session.workspacePath || "Not recorded"}</dd><dt>Updated</dt><dd>{session.updatedAt}</dd>
    </dl></details>
    {demoMode
      ? <DemoConversation session={session} />
      : <>
        <History sessionId={session.id} />
        {status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch
        ? <>
          <LiveSessionPanel store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation.capability} />
          <OwnedSessionPanel sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} />
          {status.ownedAsksEnabled && <AskPanel epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation.capability} />}
          <NotesPanel epoch={status.hostEpoch} sessionId={session.id} reader={notesReader} capability={mutation.capability} />
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation.capability} />}
        </>
        : <ReadOnlyComposer />}
      </>}
  </div>;
}

function DemoConversation({ session }: { session: WorkspaceSession }) {
  const [text, setText] = useState("");
  const [messages, setMessages] = useState([
    { role: "user", text: "Create a usable desktop workspace I can run locally." },
    { role: "assistant", text: "The first interactive workspace is running. Project and session navigation, configuration surfaces, a transcript, and this composer are ready to try." },
  ]);
  function submit() {
    const value = text.trim();
    if (!value) return;
    setMessages(current => [...current, { role: "user", text: value }, { role: "assistant", text: "Demo response: the packaged app sends this through the shared session runtime. This browser preview keeps everything in memory." }]);
    setText("");
  }
  return <section className="conversation" aria-label={`Demo conversation for ${session.title}`}>
    <div className="demo-banner"><strong>Interactive browser demo</strong><span>Messages are local and disappear on refresh. Run the packaged desktop for real sessions.</span></div>
    <div className="messages">
      {messages.map((message, index) => <article key={index} className={`message message-${message.role}`}>
        <div className="avatar">{message.role === "user" ? "You" : "A"}</div><div><strong>{message.role === "user" ? "You" : "CodeAlta"}</strong><p>{message.text}</p></div>
      </article>)}
    </div>
    <div className="composer">
      <textarea aria-label="Message" value={text} onChange={event => setText(event.target.value)} onKeyDown={event => {
        if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); submit(); }
      }} placeholder="Ask CodeAlta to work on this project…" />
      <div className="composer-footer"><span>Enter to send · Shift+Enter for a new line</span><button type="button" onClick={submit} disabled={!text.trim()}>Send <span>↑</span></button></div>
    </div>
  </section>;
}

function ConfigurationPanel({ status, selectedSession, configurationState, theme, setTheme }: {
  status: BootStatus | undefined;
  selectedSession: WorkspaceSession | undefined;
  configurationState: { snapshot?: ConfigurationSnapshot; error?: string };
  theme: Theme;
  setTheme: (theme: Theme) => void;
}) {
  const inventory = configurationState.snapshot;
  return <main className="configuration-page">
    <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Configuration</h1><p>Inspect the active desktop environment and personalize this window.</p></header>
    <div className="settings-grid">
      <section className="settings-card"><div className="settings-icon">◐</div><div><h2>Appearance</h2><p>Applied immediately to this window.</p><div className="segmented">
        <button type="button" aria-pressed={theme === "dark"} onClick={() => setTheme("dark")}>Dark</button>
        <button type="button" aria-pressed={theme === "light"} onClick={() => setTheme("light")}>Light</button>
      </div></div></section>
      <section className="settings-card"><div className="settings-icon">◆</div><div><h2>Providers &amp; models</h2><p>Current session provider: <strong>{selectedSession?.providerKey ?? "not recorded"}</strong>.</p>
        {configurationState.error && <p className="error-text">{configurationState.error}</p>}
        {!inventory && !configurationState.error && <p>Loading configured providers…</p>}
        {inventory && inventory.providers.length === 0 && <p>No provider inventory is exposed in this launch mode.</p>}
        {inventory?.providers.map(provider => <div className="inventory-row" key={provider.id}><span><strong>{provider.name}</strong><small>{provider.type} · {provider.defaultModel ?? "No default model"}</small></span><StatusPill label={provider.enabled ? "Enabled" : "Disabled"} /></div>)}
        {inventory?.providersTruncated && <p className="muted-text">Showing the first 32 configured providers.</p>}
      </div></section>
      <section className="settings-card"><div className="settings-icon">Aa</div><div><h2>Agent prompts</h2><p>Prompt selection is captured by the session runtime. Desktop editing is not exposed by the current bridge.</p><StatusPill label="Read-only in this version" /></div></section>
      <section className="settings-card"><div className="settings-icon">⌘</div><div><h2>Skills</h2><p>Skills remain project/global filesystem resources and are available to shared agent sessions.</p><StatusPill label="Managed by CodeAlta runtime" /></div></section>
      <section className="settings-card"><div className="settings-icon">⬡</div><div><h2>Plugins</h2><p>Live plugin events now originate in the shared runtime, so desktop and terminal heads observe the same publications.</p>
        {inventory?.plugins.map(plugin => <div className="inventory-row" key={plugin.id}><span><strong>{plugin.name}</strong><small>{plugin.version ?? "No version"} · {plugin.contributionCount} contributions</small></span><StatusPill label={plugin.state} /></div>)}
        {inventory && inventory.plugins.length === 0 && <StatusPill label={inventory.pluginRuntimeAvailable ? "No active plugins" : "Requires packaged host"} />}
        {inventory?.pluginsTruncated && <p className="muted-text">Showing the first 32 active plugins.</p>}
      </div></section>
      <section className="settings-card"><div className="settings-icon">i</div><div><h2>About</h2><p>{status?.productName ?? "CodeAlta Desktop"} · {status?.version ?? "initializing"}</p><p className="muted-text">Use <code>altatui</code> for provider/account/plugin mutation until those commands are exposed by the desktop bridge.</p></div></section>
    </div>
  </main>;
}

function StatusPill({ label }: { label: string }) { return <span className="status-pill">{label}</span>; }
function LoadingRows() { return <div className="loading-rows"><span /><span /><span /></div>; }
function EmptyWorkspace({ workspaceState }: { workspaceState: WorkspaceState }) {
  return <div className="empty-workspace"><div className="empty-logo">A</div><h1>{workspaceState.kind === "loading" ? "Loading your sessions…" : "Select a session"}</h1><p>Choose a project and session from the sidebar to inspect its transcript and runtime.</p></div>;
}
function shortPath(path: string) { const parts = path.replaceAll("\\", "/").split("/").filter(Boolean); return parts.slice(-2).join("/") || path; }

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
  return <section className="conversation history" aria-labelledby="history-heading">
    <div className="section-heading"><div><span className="eyebrow">Journal</span><h2 id="history-heading">Persisted history</h2></div><button type="button" className="quiet-button" onClick={() => setRequest({ sessionId, cursor: null })}>Refresh</button></div>
    {(!current || current.kind === "loading") && <p role="status">Loading persisted history…</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{historyMessage(current.code)}</p>}
    {page?.tailOmitted && <div role="status" className="banner">The malformed final journal record was omitted.</div>}
    {page?.entries.length === 0 && <div className="empty-history">No visible events in this page.</div>}
    <div className="messages">
      {page?.entries.map(entry => <article key={entry.offset} className={`message message-${entry.kind?.toLowerCase() === "user" ? "user" : "assistant"}`}>
        <div className="avatar">{entry.kind?.toLowerCase() === "user" ? "You" : "A"}</div><div className="message-body">
          <div className="message-heading"><strong>{entry.kind ?? entry.eventType}</strong><time>{formatTimestamp(entry.timestamp)}</time></div>
          {entry.name && <p><strong>{entry.name}</strong></p>}{entry.text !== null && <MarkdownContent source={entry.text} />}
          {(entry.textTruncated || entry.bodyOmitted) && <p className="muted-text">Some persisted content is not included in this preview.</p>}
          <details className="event-meta"><summary>Event metadata</summary><code>{entry.eventType} · byte {entry.offset} · {entry.providerId}</code></details>
        </div>
      </article>)}
    </div>
    {page?.next && <button type="button" className="load-more" onClick={() => setRequest({ sessionId, cursor: page.next })}>Load more history</button>}
  </section>;
}

function ReadOnlyComposer() {
  return <section className="composer catalog-composer" aria-label="Message composer">
    <textarea aria-label="Message" disabled placeholder="Start the desktop in owned-session mode to send a message." />
    <div className="composer-footer"><span>Catalog mode is read-only. Your existing `.alta` data is not modified.</span><button type="button" disabled>Send <span>↑</span></button></div>
  </section>;
}

function formatTimestamp(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

function PaneSplitter({ label, value, onResize, onReset }: {
  label: string;
  value: number;
  onResize: (delta: number) => void;
  onReset: () => void;
}) {
  const lastX = useRef<number | undefined>(undefined);
  function pointerDown(event: PointerEvent<HTMLDivElement>) {
    lastX.current = event.clientX;
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function pointerMove(event: PointerEvent<HTMLDivElement>) {
    if (lastX.current === undefined || !event.currentTarget.hasPointerCapture(event.pointerId)) return;
    const delta = event.clientX - lastX.current;
    lastX.current = event.clientX;
    onResize(delta);
  }
  function pointerEnd(event: PointerEvent<HTMLDivElement>) {
    lastX.current = undefined;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      onResize(event.key === "ArrowLeft" ? -16 : 16);
    } else if (event.key === "Home") {
      event.preventDefault();
      onReset();
    }
  }
  return <div className="pane-splitter" role="separator" aria-label={label} aria-orientation="vertical" aria-valuenow={value}
    tabIndex={0} onPointerDown={pointerDown} onPointerMove={pointerMove} onPointerUp={pointerEnd} onPointerCancel={pointerEnd}
    onDoubleClick={onReset} onKeyDown={keyDown}><span /></div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
