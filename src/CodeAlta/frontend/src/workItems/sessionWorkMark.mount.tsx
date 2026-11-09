import { StrictMode, useRef } from "react";
import { createRoot } from "react-dom/client";
import type { WorkItemRow } from "#neoastra";
import { OverflowTip } from "../OverflowTip";
import { SessionWorkMark } from "./SessionWorkMark";
import { carriedBy, workItems } from "./workItems";

// Three session rows of project p1 as the Explorer draws them: s1 carries out a task, s2 a task and a plan, and s3,
// in a narrow row, a task. The rows selected and the items opened are lists.
const row = (id: string, kind: string, status: string, title: string, runner: string): WorkItemRow => ({ id, kind, title, summary: kind === "task" ? `Why ${title}.` : null,
  category: kind === "task" ? "gap" : null, status, statusText: null, created: "2026-11-09", file: `.alta/${kind}s/${id}.md`, proposedBy: null, runner, acknowledged: false,
  runsWith: null, shortId: kind === "task" ? id.slice(-4) : null });
const items = workItems([{ projectId: "p1", truncated: false, plans: [row("2026-11-09-move-sessions-between-projects", "plan", "approved", "Move sessions between projects", "s2")],
  tasks: [row("2026-11-09-scan-the-devices-q9rh", "task", "pending", "Scan the devices in the background", "s1"),
    row("2026-11-09-report-the-provider-error-k2mz", "task", "pending", "Report the provider error", "s2"),
    row("2026-11-09-fix-the-parser-p53b", "task", "pending", "Fix the parser", "s3")] }], new Set(["s1", "s2", "s3"]));
const state = { opened: [] as string[], selected: [] as string[] };
Object.assign(window, { markFixture: state });

function Rows() {
  const rail = useRef<HTMLDivElement>(null);
  return <div className="ide-shell"><div ref={rail} className="project-rail" style={{ width: 420, padding: 24 }}>
    {[["s1", 360], ["s2", 360], ["s3", 220]].map(([id, width]) => <div key={id} className="session-row" data-session={id} style={{ width }}>
      <button type="button" title="The tooltip of the row" onClick={() => state.selected.push(id as string)}>
        <span className="session-title">A rather long title of a session, which its row cuts</span>
        <SessionWorkMark items={carriedBy(items, id as string)} onOpen={item => state.opened.push(item.id)} />
      </button></div>)}
    <OverflowTip within={rail} target=".session-row > button:first-child" label=".session-title" quiet=".session-work-mark-host" />
  </div></div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Rows /></StrictMode>);
