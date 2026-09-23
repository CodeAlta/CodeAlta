import { StrictMode, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent } from "react";
import { createRoot } from "react-dom/client";
import {
  boot, configuration, workspace, sessionDisplay, sessionRuntimeState, sessionPermissions, sessionOperations,
  sessionAsks, sessionNotes, sessionUserInput, type BootStatus, type HistoryRequest, type SessionDisplayView,
  type ConfigurationSnapshot, type WorkspaceProject, type WorkspaceSession,
} from "#neoastra";
import { loadWorkspace, sessionsForProject, workspaceNotice, type WorkspaceState } from "./workspace";
import { loadHistory, historyMessage, historySettled, mergeHistoryPage, type HistoryState, type HistoryTimeline } from "./history";
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
import { notesHeightKey, defaultNotesHeight, restoreNotesHeight, persistNotesHeight, resizeNotesHeight } from "./notesHeight";
import { createUserInputReviewer } from "./sessionUserInput";
import { UserInputPanel } from "./UserInputPanel";
import { LiveSessionPanel, LiveTextMessage, LiveToolMessage } from "./LiveSessionPanel";
import { reconcileTimeline } from "./reconcileTimeline";
import { TimelineMessage } from "./TimelineMessage";
import { latestNotes } from "./timeline";
import { bottomScrollTop, createTimelineScrollMemory } from "./timelineScroll";
import { resolveShortcut, type ShortcutAction } from "./shortcuts";
import { persistDraft, restoreDraft } from "./promptDraft";
import { constrainPaneLayout, defaultPaneLayout, persistPaneLayout, resizePane, restorePaneLayout, type PaneName } from "./paneLayout";
import { visibleConfigurationSections, type ConfigurationScope } from "./configurationSections";
import { AppIcon } from "./AppIcon";
import { sessionTime } from "./sessionTime";
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
  const [notesVisible, setNotesVisible] = useState(true);
  const [notesHeight, setNotesHeight] = useState(() => restoreNotesHeight(() => localStorage.getItem(notesHeightKey)));
  const [historyNotes, setHistoryNotes] = useState<{ sessionId: string | null; markdown: string }>({ sessionId: null, markdown: "" });
  const updateHistoryNotes = useCallback((markdown: string) => setHistoryNotes({ sessionId, markdown }), [sessionId]);
  const [dialog, setDialog] = useState<"project" | "help" | null>(null);
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
  const [scrollMemory] = useState(createTimelineScrollMemory);
  const [runtimeReader] = useState(() => createRuntimeStateReader(sessionRuntimeState.current));
  const [notesReader] = useState(() => createNotesReader(sessionNotes.current, sessionNotes.clear));
  const [inputReviewer] = useState(() => createUserInputReviewer(
    request => sessionUserInput.list(request, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.resolve({ ...request, answers: request.answers.map(answer => ({ ...answer })) }, { timeoutMilliseconds: 8000 }),
    request => sessionUserInput.cancel(request, { timeoutMilliseconds: 8000 })));
  const [permissionReviewer] = useState(() => createPermissionReviewer(sessionPermissions.list, sessionPermissions.resolve));
  const [mutation, setMutation] = useState<{ epoch: string; capability: ReturnType<typeof createMutationCapability> }>();
  const workspaceShell = useRef<HTMLDivElement>(null);
  const projectRail = useRef<HTMLElement>(null);
  const sessionRail = useRef<HTMLElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const chordPending = useRef(false);
  const [paneLayout, setPaneLayout] = useState(() => restorePaneLayout(() => localStorage.getItem(paneLayoutStorageKey), window.innerWidth));
  const [workspaceWidth, setWorkspaceWidth] = useState(window.innerWidth);
  const visiblePaneLayout = constrainPaneLayout(paneLayout, workspaceWidth);
  const [clock, setClock] = useState(Date.now);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(Date.now()), 60_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    persistPaneLayout(value => localStorage.setItem(paneLayoutStorageKey, value), paneLayout);
  }, [paneLayout]);

  useEffect(() => { persistNotesHeight(value => localStorage.setItem(notesHeightKey, value), notesHeight); }, [notesHeight]);

  useEffect(() => {
    if (view !== "workspace" || !workspaceShell.current) return;
    const shell = workspaceShell.current;
    const measure = () => setWorkspaceWidth(shell.clientWidth);
    const observer = new ResizeObserver(measure);
    observer.observe(shell);
    measure();
    return () => observer.disconnect();
  }, [view]);

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
  const owned = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);

  useEffect(() => {
    function keyDown(event: globalThis.KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target?.closest("dialog[open]")) { chordPending.current = false; return; }
      const editing = target?.matches("input, textarea, select, [contenteditable='true']") === true;
      const resolved = resolveShortcut(event, chordPending.current, editing);
      chordPending.current = resolved.chordPending;
      if (!resolved.handled) return;
      event.preventDefault();
      if (resolved.action) runShortcut(resolved.action);
    }
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  });

  function runShortcut(action: ShortcutAction) {
    const projects = snapshot?.projects ?? [];
    if (action === "expandPrompt") {
      if (!dialog) document.querySelector<HTMLButtonElement>("#expand-session-prompt")?.click();
    }
    else if (action === "openProject") setDialog("project");
    else if (action === "help") setDialog("help");
    else if (action === "escape") { setDialog(null); (document.activeElement as HTMLElement | null)?.blur(); }
    else if (action === "settings" || action === "providers" || action === "models" || action === "prompts" || action === "plugins") setView("configuration");
    else if (action === "toggleNotes") setNotesVisible(value => !value);
    else if (action === "focusPrompt") document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt")?.focus();
    else if (action === "focusSearch") searchInput.current?.focus();
    else if (action === "focusProjects") projectRail.current?.querySelector<HTMLButtonElement>('button[aria-pressed="true"]')?.focus();
    else if (action === "focusSessions") sessionRail.current?.querySelector<HTMLButtonElement>('button[aria-pressed="true"]')?.focus();
    else if (action === "nextProject" || action === "previousProject") {
      if (!projects.length) return;
      const index = Math.max(0, projects.findIndex(project => project.id === projectId));
      selectProject(projects[(index + (action === "nextProject" ? 1 : -1) + projects.length) % projects.length].id);
    } else if (action === "nextSession" || action === "previousSession") {
      if (!visibleSessions.length) return;
      const index = Math.max(0, visibleSessions.findIndex(session => session.id === sessionId));
      setSessionId(visibleSessions[(index + (action === "nextSession" ? 1 : -1) + visibleSessions.length) % visibleSessions.length].id);
    } else if (action === "context") document.querySelector<HTMLButtonElement>(".prompt-state")?.click();
  }

  function selectProject(nextProjectId: string | null) {
    setProjectId(nextProjectId);
    const nextSessions = snapshot ? sessionsForProject(snapshot, nextProjectId) : [];
    setSessionId(nextSessions[0]?.id ?? null);
    setView("workspace");
  }

  function changePane(pane: PaneName, delta: number) {
    setPaneLayout(current => {
      const width = workspaceShell.current?.clientWidth ?? workspaceWidth;
      const visible = constrainPaneLayout(current, width);
      const next = resizePane(visible, pane, delta, width);
      return next[pane] === visible[pane] ? current : next;
    });
  }

  function resetPane(pane: PaneName) {
    setPaneLayout(current => ({ ...current, [pane]: defaultPaneLayout[pane] }));
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
          "--project-pane-width": `${visiblePaneLayout.projects}px`,
          "--session-pane-width": `${visiblePaneLayout.sessions}px`,
        } as CSSProperties}>
        <aside className="project-rail" aria-label="Projects" ref={projectRail}>
          <div className="panel-title"><span>Projects</span><span><button type="button" className="rail-action" title="Open project (Ctrl+O)" onClick={() => setDialog("project")}>＋</button><span className="count">{snapshot?.projects.length ?? 0}</span></span></div>
          {workspaceState.kind === "loading" && <LoadingRows />}
          {workspaceState.kind === "unconfigured" && <div className="sidebar-empty">No catalog configured. See the launch instructions below.</div>}
          {workspaceState.kind === "error" && <div role="alert" className="sidebar-empty error-text">{workspaceState.message}</div>}
          {snapshot && <ul className="nav-list">
            {snapshot.projects.map(project => <li key={project.id}><button type="button" title={project.path} aria-pressed={projectId === project.id} onClick={() => selectProject(project.id)}>
              <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small title={project.path}>{project.path}</small>{project.archived && <small>Archived</small>}</span>
            </button></li>)}
            <li><button type="button" aria-pressed={projectId === null} onClick={() => selectProject(null)}>
              <span className="project-icon muted">◇</span><span><strong>Other sessions</strong><small>No matching project</small></span>
            </button></li>
          </ul>}
          <div className="rail-footer">
            <button type="button" className="quiet-button icon-label-button" onClick={() => setView("configuration")}><AppIcon name="settings" size={14} />Settings &amp; extensions</button>
          </div>
        </aside>

        <PaneSplitter label="Resize projects" value={visiblePaneLayout.projects} onResize={delta => changePane("projects", delta)} onReset={() => resetPane("projects")} />

        <aside className="session-rail" aria-label="Sessions" ref={sessionRail}>
          <div className="session-rail-header">
            <div><span className="eyebrow">Sessions</span><h2>{selectedProject?.name ?? "Other sessions"}</h2></div>
            <button type="button" className="icon-button" title="Refresh by relaunching the current desktop host" disabled>＋</button>
          </div>
          <label className="search"><AppIcon name="search" size={14} /><input ref={searchInput} value={search} onChange={event => setSearch(event.target.value)} placeholder="Search sessions" /></label>
          {notice && <p role="status" className="notice">{notice}</p>}
          <div className="session-list">
            {visibleSessions.map(session => <button type="button" key={session.id} aria-pressed={sessionId === session.id} onClick={() => setSessionId(session.id)}>
              <span className="session-title">{session.title}</span>
              <span className="session-meta"><span>{session.providerKey ?? "No provider"}</span><SessionTime value={session.updatedAt} now={clock} /></span>
            </button>)}
            {snapshot && visibleSessions.length === 0 && <div className="sidebar-empty">{search ? "No matching sessions." : "No sessions in this project."}</div>}
          </div>
          {notesVisible && <NotesPanel epoch={owned ? status?.hostEpoch : undefined} sessionId={sessionId}
            reader={owned ? notesReader : undefined} capability={owned ? mutation?.capability : undefined}
            fallbackMarkdown={historyNotes.sessionId === sessionId ? historyNotes.markdown : ""} onClose={() => setNotesVisible(false)}
            preferredHeight={notesHeight} onResize={delta => setNotesHeight(height => resizeNotesHeight(height, -delta))}
            onReset={() => setNotesHeight(defaultNotesHeight)} onCleared={target => { if (target === sessionId) setHistoryNotes({ sessionId: target, markdown: "" }); }} />}
          {!notesVisible && <button type="button" className="quiet-button icon-label-button show-notes" onClick={() => setNotesVisible(true)}><AppIcon name="notes" size={14} />Show Alta notes</button>}
        </aside>

        <PaneSplitter label="Resize sessions" value={visiblePaneLayout.sessions} onResize={delta => changePane("sessions", delta)} onReset={() => resetPane("sessions")} />

        <main className="content">
          {error && <div className="banner banner-error" role="alert">{error}</div>}
          {!selectedSession
            ? <EmptyWorkspace workspaceState={workspaceState} />
            : <SessionWorkspace key={selectedSession.id} session={selectedSession} status={status} mutation={mutation}
                submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
                 askActions={askActions} display={display} scrollMemory={scrollMemory} runtimeReader={runtimeReader}
                 permissionReviewer={permissionReviewer} inputReviewer={inputReviewer} configuration={configurationState.snapshot}
                 onNotesChange={updateHistoryNotes} onOpenConfiguration={() => setView("configuration")} />}
        </main>
      </div>}
    {dialog === "project" && <OpenProjectDialog projects={snapshot?.projects ?? []} onOpen={id => { selectProject(id); setDialog(null); }} onClose={() => setDialog(null)} />}
    {dialog === "help" && <ShortcutHelp onClose={() => setDialog(null)} />}
  </div>;
}

function SessionWorkspace({ session, status, mutation, submissions, steering, compaction, abortRuns, queue, askActions, display, scrollMemory, runtimeReader, permissionReviewer, inputReviewer, configuration: configurationSnapshot, onNotesChange, onOpenConfiguration }: {
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
  scrollMemory: ReturnType<typeof createTimelineScrollMemory>;
  runtimeReader: ReturnType<typeof createRuntimeStateReader>;
  permissionReviewer: ReturnType<typeof createPermissionReviewer>;
  inputReviewer: ReturnType<typeof createUserInputReviewer>;
  configuration: ConfigurationSnapshot | undefined;
  onNotesChange: (markdown: string) => void;
  onOpenConfiguration: () => void;
}) {
  const timeline = useRef<HTMLDivElement>(null);
  const [scrollSelection] = useState(() => scrollMemory.open(session.id));
  const restoreFrame = useRef(0);
  const observedDisplay = useSyncExternalStore(display.subscribe, display.getSnapshot);
  const live = status?.hostEpoch && observedDisplay.hostEpoch === status.hostEpoch && observedDisplay.sessionId === session.id
    ? observedDisplay : null;
  const [timelineFollowing, setTimelineFollowing] = useState(scrollSelection.following);
  useEffect(() => {
    const element = timeline.current;
    if (!element) return;
    let frame = 0;
    const scrollBottom = () => {
      if (!scrollSelection.following()) return;
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { element.scrollTop = bottomScrollTop(element); });
    };
    const observer = new MutationObserver(scrollBottom);
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    scrollBottom();
    return () => { observer.disconnect(); cancelAnimationFrame(frame); cancelAnimationFrame(restoreFrame.current); };
  }, [scrollSelection]);
  function restoreHistoryPosition() {
    const element = timeline.current;
    if (!element) return;
    const top = scrollSelection.settle(element);
    if (top === null) return;
    element.scrollTop = top;
    restoreFrame.current = requestAnimationFrame(() => scrollSelection.finishRestore());
  }
  const ownedSession = !!(status?.hostAvailable && status.hostEpoch && mutation?.epoch === status.hostEpoch);
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
        <div className="timeline-scroll" ref={timeline} onScroll={event => {
          setTimelineFollowing(scrollSelection.scroll(event.currentTarget));
        }}>
        <History sessionId={session.id} onNotesChange={onNotesChange} onSettled={restoreHistoryPosition}
          live={ownedSession ? live?.snapshot?.session ?? null : null} />
        {ownedSession && status?.hostEpoch
        ? <>
          <LiveSessionPanel store={display} hostEpoch={status.hostEpoch} sessionId={session.id} capability={mutation!.capability} />
          {status.ownedAsksEnabled && <AskPanel epoch={status.hostEpoch} sessionId={session.id} actions={askActions} capability={mutation!.capability} />}
          {status.ownedUserInputEnabled && <UserInputPanel epoch={status.hostEpoch} sessionId={session.id} reviewer={inputReviewer} capability={mutation!.capability} />}
        </>
        : null}
        </div>
        {!timelineFollowing && <button type="button" className="timeline-bottom-button" onClick={() => {
          if (timeline.current) scrollSelection.jump(timeline.current);
          setTimelineFollowing(true);
          if (timeline.current) timeline.current.scrollTop = bottomScrollTop(timeline.current);
        }}><AppIcon name="arrowDown" size={14} />Jump to latest</button>}
        {ownedSession && status?.hostEpoch
          ? <OwnedSessionPanel sessionId={session.id} epoch={status.hostEpoch} submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue} capability={mutation!.capability} runtimeReader={runtimeReader} permissionReviewer={status.commandReviewEnabled ? permissionReviewer : null} configuration={configurationSnapshot} />
          : <ReadOnlyComposer sessionId={session.id} provider={session.providerKey} configuration={configurationSnapshot} onOpenConfiguration={onOpenConfiguration} />}
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
        <div className="avatar"><AppIcon name={message.role === "user" ? "user" : "assistant"} size={17} /></div><div><strong>{message.role === "user" ? "You" : "CodeAlta"}</strong><p>{message.text}</p></div>
      </article>)}
    </div>
    <div className="composer">
      <textarea aria-label="Message" value={text} onChange={event => setText(event.target.value)} onKeyDown={event => {
        if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); submit(); }
      }} placeholder="Ask CodeAlta to work on this project…" />
      <div className="composer-footer"><span>Enter to send · Shift+Enter for a new line</span><button type="button" onClick={submit} disabled={!text.trim()}>Send <AppIcon name="send" size={14} /></button></div>
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
  const [scope, setScope] = useState<ConfigurationScope>("all");
  const [query, setQuery] = useState("");
  const visible = new Set(visibleConfigurationSections(scope, query));
  const mcp = inventory?.plugins.find(plugin => `${plugin.id} ${plugin.name}`.toLowerCase().includes("mcp"));
  return <main className="configuration-page">
    <header className="page-heading"><span className="eyebrow">Desktop</span><h1>Configuration</h1><p>Inspect the active desktop environment and personalize this window.</p></header>
    <div className="settings-layout">
      <aside className="settings-navigation" aria-label="Configuration sections">
        <label className="settings-search"><AppIcon name="search" size={14} /><input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder="Search settings" /></label>
        <nav>{([ ["all", "All settings"], ["general", "General"], ["agent", "Agent"], ["extensions", "Extensions"] ] as const).map(([value, label]) =>
          <button type="button" key={value} aria-pressed={scope === value} onClick={() => setScope(value)}>{label}</button>)}</nav>
        <p>Configuration is read-only unless a card explicitly offers an editable control.</p>
      </aside>
      <div className="settings-grid">
      {visible.has("appearance") && <section className="settings-card"><div className="settings-icon">◐</div><div><h2>Appearance</h2><p>Applied immediately to this window.</p><div className="segmented">
        <button type="button" aria-pressed={theme === "dark"} onClick={() => setTheme("dark")}>Dark</button>
        <button type="button" aria-pressed={theme === "light"} onClick={() => setTheme("light")}>Light</button>
      </div></div></section>}
      {visible.has("providers") && <section className="settings-card"><div className="settings-icon"><AppIcon name="model" size={19} /></div><div><h2>Providers &amp; models</h2><p>Current session provider: <strong>{selectedSession?.providerKey ?? "not recorded"}</strong>.</p>
        {configurationState.error && <p className="error-text">{configurationState.error}</p>}
        {!inventory && !configurationState.error && <p>Loading configured providers…</p>}
        {inventory && inventory.providers.length === 0 && <p>No provider inventory is exposed in this launch mode.</p>}
        {inventory?.providers.map(provider => <div className="inventory-row" key={provider.id}><span><strong>{provider.name}</strong><small>{provider.type} · {provider.defaultModel ?? "No default model"}</small></span><StatusPill label={provider.enabled ? "Enabled" : "Disabled"} /></div>)}
        {inventory?.providersTruncated && <p className="muted-text">Showing the first 32 configured providers.</p>}
      </div></section>}
      {visible.has("prompts") && <section className="settings-card"><div className="settings-icon"><AppIcon name="prompt" size={19} /></div><div><h2>Agent prompts</h2><p>The composer shows the prompt captured by an owned session. Persisted Prompt information entries include the applied system/developer text, prompt source, change summary, and token estimate.</p><StatusPill label={status?.hostAvailable ? "Session state available" : "Catalog history available"} /></div></section>}
      {visible.has("skills") && <section className="settings-card"><div className="settings-icon"><AppIcon name="tool" size={19} /></div><div><h2>Skills</h2><p>Skills remain project/global filesystem resources and are available to shared agent sessions.</p><StatusPill label="Managed by CodeAlta runtime" /></div></section>}
      {visible.has("plugins") && <section className="settings-card"><div className="settings-icon">⬡</div><div><h2>Plugins &amp; MCP</h2><p>Configured plugin policy is visible in catalog mode. Active state is shown only when the owned runtime has started that plugin.</p>
        <div className="inventory-row"><span><strong>MCP servers</strong><small>Model Context Protocol runtime state</small></span><StatusPill label={mcp ? mcp.state : inventory?.pluginRuntimeAvailable ? "Not configured" : "Runtime not started"} /></div>
        {inventory?.plugins.map(plugin => <div className="inventory-row" key={plugin.id}><span><strong>{plugin.name}</strong><small>{plugin.version ?? "No version"} · {plugin.contributionCount} contributions</small></span><StatusPill label={plugin.state} /></div>)}
        {inventory && inventory.plugins.length === 0 && <StatusPill label={inventory.pluginRuntimeAvailable ? "No active plugins" : "Requires packaged host"} />}
        {inventory?.pluginsTruncated && <p className="muted-text">Showing the first 32 active plugins.</p>}
      </div></section>}
      {visible.has("about") && <section className="settings-card"><div className="settings-icon">i</div><div><h2>About</h2><p>{status?.productName ?? "CodeAlta Desktop"} · {status?.version ?? "initializing"}</p><p className="muted-text">Use <code>altatui</code> for provider/account/plugin mutation until those commands are exposed by the desktop bridge.</p></div></section>}
      {visible.size === 0 && <div className="empty-settings"><h2>No matching settings</h2><p>Try a different search or configuration section.</p></div>}
      </div>
    </div>
  </main>;
}

function StatusPill({ label }: { label: string }) { return <span className="status-pill">{label}</span>; }

function OpenProjectDialog({ projects, onOpen, onClose }: { projects: ReadonlyArray<WorkspaceProject>; onOpen: (id: string) => void; onClose: () => void }) {
  const [query, setQuery] = useState("");
  const normalized = query.trim().toLowerCase();
  const matches = projects.filter(project => !normalized || `${project.name} ${project.path}`.toLowerCase().includes(normalized));
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section className="app-dialog" role="dialog" aria-modal="true" aria-labelledby="open-project-title">
      <header><div><span className="eyebrow">Workspace</span><h2 id="open-project-title">Open project</h2></div><button type="button" className="icon-button" aria-label="Close" title="Close" onClick={onClose}><AppIcon name="close" size={16} /></button></header>
      <label className="settings-search"><AppIcon name="search" size={14} /><input autoFocus value={query} onChange={event => setQuery(event.target.value)} placeholder="Project name or catalog path" /></label>
      <div className="dialog-list">{matches.map(project => <button type="button" key={project.id} onClick={() => onOpen(project.id)}>
        <span className="project-icon">{project.name.slice(0, 1).toUpperCase()}</span><span><strong>{project.name}</strong><small>{project.path}</small></span>
      </button>)}</div>
      {matches.length === 0 && <p className="muted-text">No catalog project matches this value. Adding a new folder requires an owned host; catalog mode never changes your `.alta` project list.</p>}
      <footer><span><kbd>Ctrl</kbd>+<kbd>O</kbd> · <kbd>Esc</kbd></span><button type="button" className="quiet-button" onClick={onClose}>Cancel</button></footer>
    </section>
  </div>;
}

function ShortcutHelp({ onClose }: { onClose: () => void }) {
  const shortcuts = [
    ["Ctrl+O", "Open project"], ["Ctrl+F", "Search sessions"], ["Alt+↑ / Alt+↓", "Previous / next session"],
    ["Alt+← / Alt+→", "Previous / next project"], ["Ctrl+,", "Configuration"], ["Ctrl+Shift+N", "Toggle Alta notes"],
    ["Ctrl+G, Ctrl+P", "Focus prompt"], ["Ctrl+G, Ctrl+S", "Focus projects"], ["Ctrl+G, Ctrl+R", "Providers"],
    ["Ctrl+G, Ctrl+O", "Models"], ["Ctrl+G, Ctrl+H", "Agent prompts"], ["Ctrl+G, Ctrl+U", "Context state"],
    ["F1 or ?", "Keyboard shortcuts"], ["Escape", "Close / cancel"], ["Enter / Shift+Enter", "Send / new line in prompt"],
    ["F6", "Expand prompt (owned session)"], ["Ctrl+Enter", "Steer in regular prompt; close in expanded editor"],
  ];
  return <div className="dialog-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}>
    <section className="app-dialog shortcut-dialog" role="dialog" aria-modal="true" aria-labelledby="shortcut-title">
      <header><div><span className="eyebrow">Keyboard first</span><h2 id="shortcut-title">Shortcuts</h2></div><button autoFocus type="button" className="icon-button" aria-label="Close" title="Close" onClick={onClose}><AppIcon name="close" size={16} /></button></header>
      <dl>{shortcuts.map(([keys, label]) => <div key={keys}><dt>{keys}</dt><dd>{label}</dd></div>)}</dl>
    </section>
  </div>;
}

function LoadingRows() { return <div className="loading-rows"><span /><span /><span /></div>; }
function EmptyWorkspace({ workspaceState }: { workspaceState: WorkspaceState }) {
  return <div className="empty-workspace"><div className="empty-logo">A</div><h1>{workspaceState.kind === "loading" ? "Loading your sessions…" : "Select a session"}</h1><p>Choose a project and session from the sidebar to inspect its transcript and runtime.</p></div>;
}
function SessionTime({ value, now }: { value: string; now: number }) {
  const { label, title, dateTime } = sessionTime(value, now);
  return <time dateTime={dateTime} title={title}>{label}</time>;
}

function History({ sessionId, onNotesChange, onSettled, live }: { sessionId: string; onNotesChange: (markdown: string) => void;
  onSettled: () => void; live: SessionDisplayView | null }) {
  const [request, setRequest] = useState<HistoryRequest>({ sessionId, cursor: null });
  const [state, setState] = useState<HistoryState>();
  const [timeline, setTimeline] = useState<HistoryTimeline>();
  useEffect(() => {
    const abort = new AbortController();
    void loadHistory(workspace.history, request, abort.signal, value => {
      setState(value);
      if (value.kind === "ready") setTimeline(current => mergeHistoryPage(current, value.request, value.page));
      else if (value.kind === "error" && value.code === "history_changed") setTimeline(undefined);
    });
    return () => abort.abort();
  }, [request]);
  useEffect(() => { onNotesChange(latestNotes(timeline?.entries ?? [])); }, [timeline, onNotesChange]);
  const current = state?.request === request ? state : undefined;
  useEffect(() => {
    if (current?.kind !== "ready" || !current.page.next || timeline?.next !== current.page.next || timeline.limitReached) return;
    const timer = window.setTimeout(() => setRequest({ sessionId, cursor: current.page.next }), 0);
    return () => window.clearTimeout(timer);
  }, [current, timeline, sessionId]);
  useLayoutEffect(() => {
    if (historySettled(current, timeline)) onSettled();
  }, [current, timeline, onSettled]);
  const items = reconcileTimeline(timeline?.entries ?? [], live);
  return <section className="conversation history" aria-labelledby="history-heading">
    <div className="section-heading"><div><span className="eyebrow">Journal + recent live window</span><h2 id="history-heading">Session timeline</h2></div><button type="button" className="quiet-button icon-label-button" onClick={() => { setTimeline(undefined); setRequest({ sessionId, cursor: null }); }}><AppIcon name="refresh" size={14} />Refresh history</button></div>
    {(!current || current.kind === "loading") && <p role="status">Loading the latest persisted history…</p>}
    {current?.kind === "error" && <p role="alert" className="error-text">{historyMessage(current.code)}</p>}
    {timeline?.tailOmitted && <div role="status" className="banner">The malformed final journal record was omitted.</div>}
    {timeline?.limitReached && <div role="status" className="banner">The timeline reached its 1,000-event display limit. Refresh to restart from the beginning.</div>}
    {timeline?.next && <button type="button" className="load-more" disabled={current?.kind === "loading"} onClick={() => setRequest({ sessionId, cursor: timeline.next })}><AppIcon name="history" size={14} />Load older history</button>}
    {items.length === 0 && current?.kind === "ready" && <div className="empty-history">No visible events in this history.</div>}
    <div className="messages">
      {items.map(item => item.source === "history" ? <TimelineMessage key={item.key} item={item.item} />
        : item.source === "liveText" ? <LiveTextMessage key={item.key} row={item.row} />
        : <LiveToolMessage key={item.key} row={item.row} />)}
    </div>
    {items.some(item => item.source !== "history") && <p className="detail live-order-note">Live rows are recent retained updates, not timestamped journal events; text/tool ordering and missing intervening activity are unknown.</p>}
  </section>;
}

function ReadOnlyComposer({ sessionId, provider, configuration, onOpenConfiguration }: {
  sessionId: string; provider: string | null; configuration?: ConfigurationSnapshot; onOpenConfiguration: () => void;
}) {
  const [text, setText] = useState(() => restoreDraft(key => localStorage.getItem(key), sessionId));
  const [message, setMessage] = useState("Draft locally; sending requires an explicitly owned desktop host.");
  useEffect(() => { persistDraft((key, value) => localStorage.setItem(key, value), key => localStorage.removeItem(key), sessionId, text); }, [sessionId, text]);
  return <section className="composer catalog-composer" aria-label="Message composer">
    <div className="prompt-options" aria-label="Session configuration">
      <label><span>Agent prompt</span><select aria-label="Agent prompt" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Model</span><select aria-label="Model" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <label><span>Reasoning</span><select aria-label="Reasoning" value="recorded" disabled><option value="recorded">Recorded by session</option></select></label>
      <button type="button" className="prompt-state" onClick={onOpenConfiguration} aria-label="Open provider configuration" title={provider ?? "Provider not recorded"}><AppIcon name="settings" size={13} /><strong>{configuration?.providers.length ?? 0} providers</strong></button>
      <span className="prompt-state"><span>Context / MCP</span><strong>Requires runtime</strong></span>
    </div>
    <textarea id="catalog-prompt" aria-label="Message" maxLength={32768} value={text} onChange={event => setText(event.target.value)}
      onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); setMessage("This explicit catalog-only launch is read-only; your draft remains saved."); } }}
      placeholder="Draft a prompt for this session…" />
    <div className="composer-footer"><span role="status">{message}</span><button type="button" disabled={!text.trim()} onClick={() => setMessage("This explicit catalog-only launch is read-only; your draft remains saved.")}>Send <AppIcon name="send" size={14} /></button></div>
  </section>;
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
  function keyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
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
