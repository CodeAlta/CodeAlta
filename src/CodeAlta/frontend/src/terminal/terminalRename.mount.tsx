import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { TerminalItem } from "#neoastra";
import { RenamePopover } from "../RenamePopover";
import { TerminalList } from "./TerminalList";

function Fixture() {
  const [terminal, setTerminal] = useState<TerminalItem>({
    id: "terminal", projectId: "project", sessionId: null, title: "Build shell", titled: true,
    folder: "/work/build", profile: "sh", profileName: "sh", programTitle: null, running: true, exitCode: null,
    integrated: false, busy: false, command: null, lastExitCode: null, columns: 80, rows: 24,
    created: "2026-11-22T00:00:00Z", open: false, attention: false, agent: false,
  });
  const [editing, setEditing] = useState<"session" | "project" | null>(null);
  const [requests, setRequests] = useState<{ id: string; title: string }[]>([]);
  const record = (id: string, title: string) => setRequests(current => [...current, { id, title }]);
  return <main style={{ width: 272, padding: 12 }}>
    <TerminalList terminals={[terminal]} active={null} onOpen={() => {}} onClose={() => {}}
      rename={async (id, title) => {
        record(id, title);
        // The host publishes the folder title when the custom title is removed.
        setTerminal(current => ({ ...current, title: title || current.folder, titled: title !== "" }));
        return "ok";
      }} />
    {(["session", "project"] as const).map(kind => <div key={kind} style={{ position: "relative" }}>
      <button id={`edit-${kind}`} type="button" onClick={() => setEditing(kind)}>Rename {kind}</button>
      {editing === kind && <RenamePopover label={kind === "session" ? "Session title" : "Project name"} initialValue="Existing name"
        busy={false} onSubmit={title => record(kind, title)} onCancel={() => setEditing(null)} />}
    </div>)}
    <output id="rename-requests">{JSON.stringify(requests)}</output>
  </main>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
