// Disposable page: a prompt (a text area) with the issue picker of the composers. Driven by issuePicker.browser.test.ts
// through the buttons and the outputs below.
import { StrictMode, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { IssuePicker } from "./IssuePicker";
import { ProjectReferenceContext } from "./ProjectReferencePicker";
import type { PromptInput } from "./PromptEditor";
import { dismissDialogsOnOutsidePress } from "./modalDialogs";

document.documentElement.classList.add("bp6-dark");
// As the application does: a press outside a modal window dismisses it, unless the window keeps what it holds.
dismissDialogsOnOutsidePress(document);

function Fixture() {
  const input = useRef<HTMLTextAreaElement>(null);
  const [text, setText] = useState("");
  const [request, setRequest] = useState(0);
  const [mounted, setMounted] = useState(true);
  const [attached, setAttached] = useState<number[]>([]);
  return <ProjectReferenceContext.Provider value={{ expectedEpoch: "epoch", projectId: "project", projectPath: "/work/repo", sessionId: null }}>
    <output id="attached">{attached.join(",")}</output>
    {/* Add issues of the attach menu. */}
    <button id="attach" type="button" onClick={() => setRequest(value => value + 1)}>Add issues</button>
    {/* What a Send does: the composer hides the picker, then shows it again. */}
    <button id="remount" type="button" onClick={() => { setMounted(false); setTimeout(() => setMounted(true), 50); }}>Remount</button>
    <textarea id="prompt" ref={input} value={text} onChange={event => setText(event.target.value)} />
    {mounted && <IssuePicker edit={setText} input={input as React.RefObject<PromptInput | null>} openRequest={request}
      onAttach={issues => setAttached(current => [...current, ...issues.map(issue => issue.number)])} />}
  </ProjectReferenceContext.Provider>;
}
// Under StrictMode, as in the application: React then runs each effect of a new component twice.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
