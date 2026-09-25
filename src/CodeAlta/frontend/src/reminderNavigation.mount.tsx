import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { OwnedSessionPanel } from "./OwnedSessionPanel";
import { ReminderPanel } from "./ReminderPanel";
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
  const [view, setView] = useState<"workspace" | "reminders">("workspace");
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
  useLayoutEffect(() => {
    const keyDown = (event: KeyboardEvent) => dispatchWorkspaceShortcut(event, chord.current, {
      workspaceActive: view === "workspace", workspaceShell: shell.current, modalOpen: modal,
      selectedProjectFocused: false, infoTrigger: infoButton.current, reminderTrigger: button.current,
      infoSelection,
      selection: epoch === "e1" && capability.canMutate() && infoSelection
        ? { epoch, ...infoSelection } : null,
      run: action => { if (action === "sessionInfo") infoButton.current?.click();
        else if (action === "reminders") button.current?.click(); else if (action === "escape") setModal(false); },
    });
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  }, [view, session, epoch, project, modal, capability, infoSelection]);
  function openReminders() {
    const captured = current.current;
    if (captured.view !== "workspace" || captured.modal || captured.epoch !== "e1" || !capability.canMutate()
      || captured.session !== session || captured.project !== project || captured.epoch !== epoch) return;
    setView("reminders");
  }
  return <div id="workspace-shell" ref={shell} data-ambiguous={ambiguous} data-project={project} data-session={session}>
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
        reminderId: request.reminderId, content: null, delaySeconds: null, repeatCount: null })} />}
  </div>;
}
createRoot(document.getElementById("app")!).render(<App />);
