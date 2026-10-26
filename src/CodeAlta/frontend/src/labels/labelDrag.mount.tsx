// The titles of three labels and the row of a project under the first, with the pointer drag of the Explorer: what
// a drag drops, where it would drop while it moves, and the clicks that reach the rows.
import { createRoot } from "react-dom/client";
import { useRailDrag } from "./labelDrag";

type Recorded = { clicks: string[]; drops: string[] };
const recorded: Recorded = { clicks: [], drops: [] };
(window as unknown as { recorded: Recorded }).recorded = recorded;

const labels = ["infra", "helm", "tools"];

function Fixture() {
  const drag = useRailDrag((dragged, drop) => !(dragged.kind === "label" && dragged.labelId === drop.labelId),
    (dragged, drop, copy) => recorded.drops.push(dragged.kind === "label" ? `${dragged.labelId}:${drop.place}:${drop.labelId}`
      : `${dragged.projectId}:${dragged.from}->${drop.labelId || "none"}${copy ? "+" : ""}`));
  return <ul style={{ listStyle: "none", margin: 0, padding: 0, width: 300 }}>
    {labels.map(id => <li key={id} id={id} data-label-drop={id} style={{ height: 40 }} data-drop={drag.view?.over?.labelId === id ? drag.view.over.place : undefined}
      {...drag.source({ kind: "label", labelId: id }, id)}>
      <button type="button" style={{ width: "100%", height: 40 }} onClick={() => recorded.clicks.push(id)}>{id}</button>
    </li>)}
    <li id="project" style={{ height: 40 }} {...drag.source({ kind: "project", projectId: "alpha", from: "infra" }, "alpha")}>
      <button type="button" style={{ width: "100%", height: 40 }} onClick={() => recorded.clicks.push("alpha")}>alpha</button>
    </li>
    <li id="other" data-label-drop="" style={{ height: 40 }}>Other projects</li>
    {drag.view && <li id="ghost" style={{ position: "fixed", left: drag.view.x, top: drag.view.y, pointerEvents: "none" }}>{drag.view.name}</li>}
  </ul>;
}

createRoot(document.getElementById("root")!).render(<Fixture />);
