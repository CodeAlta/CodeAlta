// Disposable page: a row of the Explorer with the question that is asked beside it, in its two forms. Driven by
// confirmPopover.browser.test.ts through the buttons and the counters below.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { ConfirmPopover } from "./ConfirmPopover";

document.documentElement.classList.add("bp6-dark");

function Fixture() {
  const [asked, setAsked] = useState<"archive" | "delete" | null>(null);
  const [answers, setAnswers] = useState<string[]>([]);
  const answer = (value: string) => { setAnswers(current => [...current, value]); setAsked(null); };
  return <div className="ide-shell" style={{ width: 272, padding: 12 }}>
    <output id="answers">{answers.join(",")}</output>
    {/* What a menu does when it closes: its own button takes the focus back a moment after the question is asked. */}
    <button id="menu" type="button" onClick={event => { const button = event.currentTarget; setAsked("delete"); setTimeout(() => button.focus(), 60); }}>Menu</button>
    <button id="archive" type="button" onClick={() => setAsked("archive")}>Ask to archive</button>
    <button id="delete" type="button" onClick={() => setAsked("delete")}>Ask to delete</button>
    <div className="project-action-row" style={{ position: "relative" }}>
      <button id="row" type="button">Project</button>
      {asked === "archive" && <ConfirmPopover title="Archive this project?" subject="Project" detail="Its files and its sessions are kept."
        confirmLabel="Archive" busy={false} onConfirm={remember => answer(`archive:${remember}`)} onCancel={() => answer("cancel")} />}
      {asked === "delete" && <ConfirmPopover always title="Delete this project?" subject="Project" detail="Its sessions are deleted."
        confirmLabel="Delete" intent="danger" busy={false} onConfirm={() => answer("delete")} onCancel={() => answer("cancel")} />}
    </div>
  </div>;
}
// Under StrictMode, as in the application: React then runs each effect of a new component twice.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
