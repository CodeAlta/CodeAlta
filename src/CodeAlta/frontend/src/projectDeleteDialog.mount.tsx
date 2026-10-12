import { useState } from "react";
import { createRoot } from "react-dom/client";
import { ProjectDeleteDialog } from "./ProjectDeleteDialog";

function Fixture() {
  const [open, setOpen] = useState(true);
  const [count, setCount] = useState(0);
  return <><output id="confirmations">{count}</output><button id="reopen" onClick={() => setOpen(true)}>Reopen</button>
    {open && <ProjectDeleteDialog target={{ epoch: "fixture", id: "p", path: "/synthetic/source", name: "Archived project", archived: true, generation: 1 }}
      busy={false} disabled={false} error="" onCancel={() => setOpen(false)} onConfirm={() => { setCount(value => value + 1); setOpen(false); }} />}</>;
}
createRoot(document.getElementById("root")!).render(<Fixture />);
