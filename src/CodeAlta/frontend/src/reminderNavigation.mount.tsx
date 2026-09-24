import { useEffect, useRef, useState } from "react";
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
import type { ReminderCreateRequest } from "#neoastra";

const unavailable = async (): Promise<never> => { throw new Error("fixture must not submit a composer operation"); };
const writes: ReminderCreateRequest[] = [];
const actions = createReminderActions(async request => {
  writes.push(request);
  return { status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId, reminderId: "created" };
}, unavailable);
const selections = createNextSendSelectionStore(key => localStorage.getItem(key), (key, value) => localStorage.setItem(key, value));
const drafts = createDraftIndicators();
const fixture = { writes, session: (_id: string) => {}, host: (_epoch: string | null) => {},
  workspace: () => {}, modal: (_open: boolean) => {}, setProject: (_id: string | null) => {} };
Object.assign(window, { reminderNavigationFixture: fixture });

function App() {
  const [session, setSession] = useState("one");
  const [epoch, setEpoch] = useState<string | null>("e1");
  const [project, setProject] = useState<string | null>("project");
  const [view, setView] = useState<"workspace" | "reminders">("workspace");
  const [modal, setModal] = useState(false);
  const shell = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const chord = useRef<WorkspaceShortcutState>({ chordPending: false, sessionInfoPrefix: false, reminderPrefix: null });
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
  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => dispatchWorkspaceShortcut(event, chord.current, {
      workspaceActive: view === "workspace", workspaceShell: shell.current, modalOpen: modal,
      selectedProjectFocused: false, infoTrigger: null, reminderTrigger: button.current,
      selection: epoch === "e1" && capability.canMutate() && view === "workspace" ? { epoch, sessionId: session, projectId: project } : null,
      run: action => { if (action === "reminders") button.current?.click(); else if (action === "escape") setModal(false); },
    });
    window.addEventListener("keydown", keyDown);
    return () => window.removeEventListener("keydown", keyDown);
  }, [view, session, epoch, project, modal, capability]);
  function openReminders() {
    const captured = current.current;
    if (captured.view !== "workspace" || captured.modal || captured.epoch !== "e1" || !capability.canMutate()
      || captured.session !== session || captured.project !== project || captured.epoch !== epoch) return;
    setView("reminders");
  }
  return <div id="workspace-shell" ref={shell}>
    {view === "workspace" && epoch === "e1" && <div className="session-workspace">
      <OwnedSessionPanel key={JSON.stringify([epoch, session])} sessionId={session} epoch={epoch}
        submissions={submissions} steering={steering} compaction={compaction} abortRuns={abortRuns} queue={queue}
        capability={capability} runtimeReader={runtimeReader} permissionReviewer={null}
        draftIndicators={drafts} selections={selections} remindersTrigger={button} onOpenReminders={openReminders} />
      {modal && <div role="dialog" aria-modal="true"><input aria-label="Modal input" /></div>}
    </div>}
    {view === "reminders" && <ReminderPanel key={JSON.stringify([epoch, session])}
      target={epoch ? { epoch, sessionId: session } : null} actions={actions}
      mutationAllowed={capability.canMutate()} canMutate={capability.canMutate}
      read={async request => ({ status: "ok", epoch: request.expectedEpoch, sessionId: request.sessionId,
        activeCount: 0, completedCount: 0, reminders: [] })} />}
  </div>;
}
createRoot(document.getElementById("app")!).render(<App />);
