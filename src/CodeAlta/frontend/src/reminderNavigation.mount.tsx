import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ReminderPanel } from "./ReminderPanel";
import { McpServersPanel } from "./McpServersPanel";
import { CommandPalette } from "./CommandPalette";
import { paletteAvailable, paletteShortcut, restorePaletteFocus, type PaletteAction, type PaletteContext } from "./paletteActions";
import { createReminderActions } from "./reminderActions";
import { dispatchWorkspaceShortcut, type WorkspaceShortcutState } from "./workspaceShortcutDispatch";
import { createMutationCapability, createOwnedSubmissions } from "./sessionOperations";
import { createSteeringSubmissions } from "./sessionSteering";
import { createCompactionSubmissions } from "./sessionCompaction";
import { createAbortRunSubmissions } from "./sessionAbortRun";
import { createQueueSubmissions } from "./sessionQueue";
import { createRuntimeStateReader } from "./runtimeState";
import { createDraftIndicators } from "./promptDraft";
import { createNextSendSelectionStore } from "./nextSendSelection";
import { SessionInfoDialog } from "./SessionInfoDialog";
import { selectedSessionInfoSelection, sessionInfoView } from "./sessionInfo";
import type { ReminderCreateRequest, WorkspaceSession, WorkspaceSnapshot } from "#neoastra";

const sessions: WorkspaceSession[] = ["one", "two"].map(id => ({
  id, title: `Title ${id}`, fullTitle: `Title ${id}`, fullTitleTruncated: false, parentSessionId: null,
  scopeKind: "project", projectId: "project", lineageIssue: null, workspacePath: "/fixture/project",
  providerKey: "fixture", updatedAt: "2026-09-24T00:00:00Z",
}));
const catalog: WorkspaceSnapshot = { configured: true,
  projects: [{ id: "project", name: "Fixture project", path: "/fixture/project", archived: false }],
  sessions, projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
};

const unavailable = async (): Promise<never> => { throw new Error("fixture must not submit a composer operation"); };
const writes: ReminderCreateRequest[] = [];
const actions = createReminderActions(async request => {
  writes.push(request);
  return { status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId, reminderId: "created" };
}, unavailable);
const selections = createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value));
const drafts = createDraftIndicators();
const fixture = { writes, session: (_id: string) => {}, host: (_epoch: string | null) => {},
  workspace: () => {}, modal: (_open: boolean) => {}, setProject: (_id: string | null) => {},
  ambiguous: (_value: boolean) => {} };
Object.assign(window, { reminderNavigationFixture: fixture });

function App() {
  const [session, setSession] = useState("one");
  const [epoch, setEpoch] = useState<string | null>("e1");
  const [project, setProject] = useState<string | null>("project");
  const [view, setView] = useState<"workspace" | "reminders" | "mcp" | "configuration" | "providers" | "models" | "prompts">("workspace");
  const [paletteOpen, setPaletteOpen] = useState(false);
  const captured = useRef<PaletteContext | null>(null);
  const pending = useRef<{ action: PaletteAction; context: PaletteContext } | null>(null);
  const origin = useRef<HTMLElement | null>(null);
  const [modal, setModal] = useState(false);
  const [ambiguous, setAmbiguous] = useState(false);
  const [infoOpen, setInfoOpen] = useState(false);
  const shell = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const infoButton = useRef<HTMLButtonElement>(null);
  const chord = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: null, reminderPrefix: null });
  const [capability] = useState(() => createMutationCapability("e1"));
  const [submissions] = useState(() => createOwnedSubmissions(unavailable, unavailable));
  const [steering] = useState(() => createSteeringSubmissions(unavailable));
  const [compaction] = useState(() => createCompactionSubmissions(unavailable));
  const [abortRuns] = useState(() => createAbortRunSubmissions(unavailable));
  const [queue] = useState(() => createQueueSubmissions(unavailable, unavailable));
  const current = useRef({ session, epoch, project, view, modal });
  current.current = { session, epoch, project, view, modal };
  const [runtimeReader] = useState(() => createRuntimeStateReader(async () =>
    ({ status: "ok", hostEpoch: "e1", sessionId: current.current.session, entry: null,
      runtimeInstanceId: "fixture-runtime", coordinatorTransitionInProgress: false })));
  fixture.session = id => { setSession(id); setView("workspace"); };
  fixture.host = value => { setEpoch(value); setView("workspace"); };
  fixture.workspace = () => setView("workspace");
  fixture.modal = setModal;
  fixture.setProject = id => { setProject(id); setView("workspace"); };
  fixture.ambiguous = setAmbiguous;
  const duplicate = sessions.find(value => value.id === session);
  const snapshot = ambiguous && duplicate ? { ...catalog, sessions: [...sessions, { ...duplicate }] } : catalog;
  const selectedSession = snapshot.sessions.find(value => value.id === session);
  const infoSelection = selectedSessionInfoSelection(snapshot, selectedSession, project, session, project);
  function paletteContext(): PaletteContext {
    return { workspace: view === "workspace", selection: infoSelection,
      epoch: epoch === "e1" && capability.canMutate() ? epoch : null,
      infoReady: !!infoButton.current?.isConnected && infoButton.current.getAttribute("aria-expanded") === "false",
      promptReady: !!document.querySelector("#session-prompt, #catalog-prompt"), searchReady: false };
  }
  function openPalette() {
    if (paletteOpen || modal || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    origin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    captured.current = paletteContext(); setPaletteOpen(true);
  }
  function closePalette() {
    const element = origin.current;
    const previousView = view;
    setPaletteOpen(false);
    requestAnimationFrame(() => restorePaletteFocus(element, current.current.view === previousView,
      !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')));
  }
  useLayoutEffect(() => {
    if (paletteOpen || !pending.current) return;
    const { action, context } = pending.current;
    pending.current = null;
    if (modal || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]') ||
      !paletteAvailable(action, context, paletteContext())) return;
    if (action === "sessionInfo") infoButton.current?.click();
    else if (action === "focusPrompt") document.querySelector<HTMLTextAreaElement>("#session-prompt, #catalog-prompt")?.focus();
    else if (action !== "focusSearch") setView(action === "settings" ? "configuration" : action);
  });
  useLayoutEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (paletteShortcut(event, paletteOpen || modal || !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]'))) {
        event.preventDefault(); openPalette(); return;
      }
      dispatchWorkspaceShortcut(event, chord.current, {
      workspaceActive: view === "workspace", workspaceShell: shell.current, modalOpen: modal,
      selectedProjectFocused: false, infoTrigger: infoButton.current, reminderTrigger: button.current,
      infoSelection,
      selection: epoch === "e1" && capability.canMutate() && infoSelection
        ? { epoch, ...infoSelection } : null,
      run: action => { if (action === "sessionInfo") infoButton.current?.click();
        else if (action === "reminders") button.current?.click(); else if (action === "escape") setModal(false); },
      });
    };
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  }, [view, session, epoch, project, modal, capability, infoSelection, paletteOpen]);
  function openReminders() {
    const captured = current.current;
    if (captured.view !== "workspace" || captured.modal || captured.epoch !== "e1" || !capability.canMutate()
      || captured.session !== session || captured.project !== project || captured.epoch !== epoch) return;
    setView("reminders");
  }
  return <div id="workspace-shell" ref={shell} data-ambiguous={ambiguous} data-project={project} data-session={session}>
    <header className="topbar"><div className="brand"><span className="brand-mark">A</span><span>CodeAlta</span>
      <button type="button" className="project-rail-toggle" aria-label="Open command palette" onClick={openPalette}>Commands <kbd>Ctrl+P</kbd></button>
    </div></header>
    {view === "workspace" && <div className="session-workspace">
      {selectedSession && <><button type="button" ref={infoButton} aria-expanded={infoOpen} onClick={() => setInfoOpen(true)}>Session info</button>
        {infoOpen && <SessionInfoDialog info={sessionInfoView(snapshot, selectedSession, project)} demo={false}
          onClose={() => setInfoOpen(false)} />}</>}
      {epoch === "e1" ? <OwnedSessionPanel key={JSON.stringify([epoch, session])} sessionId={session} epoch={epoch}
        submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
        capability={capability} runtimeReader={runtimeReader} permissionReviewer={null}
        draftIndicators={drafts} selections={selections} remindersTrigger={button} onOpenReminders={openReminders} />
        : <textarea id="catalog-prompt" aria-label="Catalog prompt" />}
      {modal && <div role="dialog" aria-modal="true"><input aria-label="Modal input" /></div>}
    </div>}
    {view === "reminders" && <ReminderPanel key={JSON.stringify([epoch, session])}
      target={epoch ? { epoch, sessionId: session } : null} actions={actions}
      mutationAllowed={capability.canMutate()} canMutate={capability.canMutate}
      read={async request => ({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
        activeCount: 0, completedCount: 0, reminders: [] })}
      readDetail={async request => ({ status: "missing_reminder", epoch: request.expectedEpoch, sessionId: request.sessionId,
        reminderId: request.reminderId, content: null, delaySeconds: null, repeatCount: null, editRevision: null })} />}
    {view === "mcp" && <McpServersPanel target={epoch ? { epoch, sessionId: session, projectId: project } : null}
      read={async request => ({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
        projectId: project, servers: [], sources: project ? ["Global: read", "Project: read"] : ["Global: read"],
        omitted: 0, policyReadError: false })} />}
    {view !== "workspace" && view !== "reminders" && view !== "mcp" && <main aria-label={view}>{view}</main>}
    {paletteOpen && captured.current && <CommandPalette captured={captured.current} context={paletteContext()}
      onClose={closePalette} onChoose={action => {
        if (!captured.current || !paletteAvailable(action, captured.current, paletteContext())) return;
        pending.current = { action, context: captured.current }; setPaletteOpen(false);
      }} />}
  </div>;
}
createRoot(document.getElementById("app")!).render(<App />);
