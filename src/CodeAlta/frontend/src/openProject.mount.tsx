import { useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkspaceDirectoryCompletionResponse, WorkspaceOpenProjectResponse, WorkspaceSnapshot } from "#neoastra";
import { OpenProjectDialog } from "./OpenProjectDialog";
import { createProjectOpening } from "./projectOpening";
import { savedProjectSelection } from "./savedProjectSelection";
import { createMutationCapability } from "./sessionOperations";
import { sessionsForProject } from "./workspace";

const projects = [
  { id: "one", name: "Alpha", path: "C:/catalog/alpha", archived: false },
  { id: "two", name: "Beta", path: "C:/catalog/beta", archived: false },
  { id: "old", name: "Legacy", path: "C:/catalog/legacy", archived: true },
];
const sessions = projects.map(project => ({ id: `session-${project.id}`, title: project.name, fullTitle: project.name,
  createdAt: null, fullTitleTruncated: false, parentSessionId: null, lineageIssue: null, scopeKind: "project" as const,
  projectId: project.id, workspacePath: project.path, providerKey: null, updatedAt: "2026-09-24T00:00:00Z" }));
const original: WorkspaceSnapshot = { configured: true, projects, sessions,
  projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false };
const calls: { confirmed: boolean; path: string; resolve: (reply: WorkspaceOpenProjectResponse) => void;
  reject: (error: Error) => void }[] = [];
const completionCalls: { request: { expectedHostEpoch: string; directoryPath: string; prefix: string }; signal: AbortSignal;
  resolve: (reply: WorkspaceDirectoryCompletionResponse) => void; reject: (error: Error) => void }[] = [];
const opening = createProjectOpening(request => new Promise((resolve, reject) =>
  calls.push({ confirmed: request.confirmed, path: request.directoryPath, resolve, reject })));
const capability = createMutationCapability("12345678-1234-1234-1234-123456789abc");
const fixture = { calls, completionCalls, current: original as WorkspaceSnapshot | undefined, owned: true, demo: false, failRefresh: false,
  set: (_snapshot: WorkspaceSnapshot | undefined) => {}, stale: (_snapshot: WorkspaceSnapshot | undefined) => {},
  host: (_owned: boolean) => {}, open: () => {}, refresh: () => {}, selected: "one", session: "session-one" };
Object.assign(window, { openProjectFixture: fixture });

function App() {
  const [snapshot, setSnapshot] = useState<WorkspaceSnapshot | undefined>(original);
  const [owned, setOwned] = useState(true);
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState("preserved draft");
  const current = useRef(snapshot);
  fixture.set = next => { fixture.current = next; current.current = next; setSnapshot(next); };
  fixture.stale = next => { fixture.current = next; current.current = next; };
  fixture.host = next => { fixture.owned = next; setOwned(next); };
  fixture.open = () => setOpen(true);
  fixture.refresh = () => { fixture.set({ ...current.current!, projectsTruncated: true }); };
  return <div className={document.documentElement.dataset.theme === "light" ? "app theme-light" : "app theme-dark"}>
    <button type="button" onClick={() => setOpen(true)}>Open project</button>
    <p id="selection">{fixture.selected} / {fixture.session}</p>
    <textarea aria-label="Session draft" value={draft} onChange={event => setDraft(event.target.value)} />
    {open && <OpenProjectDialog snapshot={snapshot} getCurrentSnapshot={() => current.current}
      epoch={owned ? "12345678-1234-1234-1234-123456789abc" : undefined}
      capability={owned ? capability : undefined} opening={opening} allowCompletion={!fixture.demo}
      getCurrentEpoch={() => fixture.owned ? "12345678-1234-1234-1234-123456789abc" : undefined}
      getCurrentScope={() => ({ projectId: fixture.selected, sessionId: fixture.session })}
      completeDirectory={(request, options) => new Promise((resolve, reject) => completionCalls.push({ request, signal: options.signal, resolve, reject }))}
      onOpen={shown => {
        if (snapshot !== current.current || !savedProjectSelection(shown, current.current)) return false;
        fixture.selected = shown.id;
        fixture.session = sessionsForProject(current.current!, shown.id)[0]?.id ?? "none";
        setOpen(false);
        return true;
      }} onRefresh={async () => { if (fixture.failRefresh) return undefined; fixture.refresh(); return { configured: true }; }}
      onImported={async () => false} onClose={() => setOpen(false)} />}
  </div>;
}
createRoot(document.getElementById("app")!).render(<App />);
