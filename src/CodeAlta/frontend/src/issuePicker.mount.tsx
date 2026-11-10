// Disposable page: a prompt (a text area) with the `#` reference picker of the composers. Driven by issuePicker.browser.test.ts
// through the buttons and the outputs below.
import { StrictMode, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { ReferencePicker } from "./ReferencePicker";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import type { PromptInput } from "./PromptEditor";
import { dismissDialogsOnOutsidePress } from "./modalDialogs";
import { ReferenceSourcesContext, type ReferenceSources } from "./referenceItems";
import type { WorkItem } from "./workItems/workItems";

// Besides the three issues of the host (updated 2026-10-18): a newer task, an older plan, and a session newer than all.
const row = (id: string, kind: string, title: string, created: string, shortId: string | null): WorkItem => ({ id, kind, title, summary: null, category: "problem",
  status: kind === "task" ? "pending" : "approved", statusText: null, created, file: `.alta/${kind}s/${id}.md`, proposedBy: null, runner: null, acknowledged: false,
  runsWith: null, tags: null, shortId, updated: null, projectId: "project", stage: "todo" });
const sources: ReferenceSources = {
  workItems: projectId => projectId === "project" ? [row("2026-11-08-fix-the-parser-p53b", "task", "Fix the parser", "2026-11-08", "p53b"),
    row("2026-10-01-release", "plan", "Release [plan]", "2026-10-01", null)] : [],
  sessions: projectId => projectId === "project" ? [{ id: "s-run", title: "Parser review", updatedAt: "2026-11-09T10:00:00Z", running: true }] : [],
};

document.documentElement.classList.add("bp6-dark");
// As the application does: a press outside a modal window dismisses it, unless the window keeps what it holds.
dismissDialogsOnOutsidePress(document);

function Fixture() {
  const input = useRef<HTMLTextAreaElement>(null);
  const [text, setText] = useState("");
  const [request, setRequest] = useState(0);
  const [mounted, setMounted] = useState(true);
  const [attached, setAttached] = useState<number[]>([]);
  // As the prompt of a new session does: attachments are refused while a modal window is open.
  const [refused, setRefused] = useState(0);
  return <ReferenceSourcesContext.Provider value={sources}><ProjectReferenceContext.Provider value={{ expectedEpoch: "epoch", projectId: "project", projectPath: "/work/repo", sessionId: null }}>
    <output id="attached">{attached.join(",")}</output>
    <output id="refused">{refused}</output>
    {/* Add issues of the attach menu. */}
    <button id="attach" type="button" onClick={() => setRequest(value => value + 1)}>Add issues</button>
    {/* What a Send does: the composer hides the picker, then shows it again. */}
    <button id="remount" type="button" onClick={() => { setMounted(false); setTimeout(() => setMounted(true), 50); }}>Remount</button>
    <textarea id="prompt" ref={input} value={text} onChange={event => setText(event.target.value)} />
    {mounted && <ReferencePicker edit={setText} input={input as React.RefObject<PromptInput | null>} openRequest={request}
      onAttach={issues => document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]') ? setRefused(value => value + 1)
        : setAttached(current => [...current, ...issues.map(issue => issue.number)])} />}
  </ProjectReferenceContext.Provider></ReferenceSourcesContext.Provider>;
}
// Under StrictMode, as in the application: React then runs each effect of a new component twice.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
