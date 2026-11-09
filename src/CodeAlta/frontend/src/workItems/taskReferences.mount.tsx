import { StrictMode, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import type { WorkItemRow } from "#neoastra";
import { MarkdownContent, MarkdownLinkScopeContext } from "../MarkdownContent";
import { findTaskReference, taskReferenceIndex, TaskReferencesContext, type TaskReferences } from "./taskReferences";
import { workItems } from "./workItems";

// A message of a session of project p1 that names a task that is done, one that waits, one that does not exist, and a task in a
// code block and in a link, which stay what they are. The tasks opened are a list; a task can be removed.
const row = (id: string, status: string, title: string, summary: string | null): WorkItemRow => ({ id, kind: "task", title, summary, category: "problem", status, statusText: null,
  created: "2026-11-08", file: `.alta/tasks/${id}.md`, proposedBy: null, runner: null, acknowledged: false, runsWith: null, shortId: id.slice(-4), updated: null });
const source = "Closed: `p53b`. Still open: `q9rh`. Unknown: `zzzz`.\n\n```\np53b\n```\n\nIn a link: [`q9rh`](https://remote.invalid/q9rh)";
const state = { opened: [] as string[], remove: (_id: string) => { } };
Object.assign(window, { taskFixture: state });

function Message() {
  const [rows, setRows] = useState([row("2026-11-08-fix-the-parser-p53b", "done", "Fix the parser", null),
    row("2026-11-08-report-the-provider-error-q9rh", "pending", "Report the provider error", "The model list shows nothing when a provider fails.")]);
  state.remove = id => setRows(current => current.filter(item => item.id !== id));
  const references = useMemo<TaskReferences>(() => {
    const index = taskReferenceIndex(workItems([{ projectId: "p1", tasks: rows, plans: [], truncated: false }], new Set()));
    return { find: (scope, text) => findTaskReference(index, scope?.sessionId === "s1" ? "p1" : null, text), open: item => { state.opened.push(item.id); } };
  }, [rows]);
  return <div style={{ width: 640, padding: 24 }}><TaskReferencesContext.Provider value={references}>
    <MarkdownLinkScopeContext.Provider value={{ sessionId: "s1" }}><MarkdownContent source={source} /></MarkdownLinkScopeContext.Provider>
  </TaskReferencesContext.Provider></div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Message /></StrictMode>);
