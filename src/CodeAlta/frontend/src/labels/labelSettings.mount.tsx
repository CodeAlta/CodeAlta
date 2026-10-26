// The page of Settings that organizes the labels, on a host that keeps three labels in memory: what the page asks
// of it is recorded for the test.
import { createRoot } from "react-dom/client";
import type { LabelItem } from "#neoastra";
import { LabelSettings } from "./LabelSettings";
import { createLabelsHub } from "./labelsHub";

const calls: string[] = [];
(window as unknown as { calls: string[] }).calls = calls;
const item = (id: string, name: string, look: Partial<LabelItem> = {}): LabelItem =>
  ({ id, name, description: null, icon: null, color: "#2d72d2", order: 1, parent: null, favorite: false, space: null, path: name, projectIds: [], ...look });
const listed: LabelItem[] = [item("infra", "Infra", { projectIds: ["alpha"] }), item("helm", "Helm", { parent: "infra" }), item("tools", "Tools", { order: 2 })];
const done = { status: "ok", message: null, label: null };
const hub = createLabelsHub({
  list: async () => ({ status: "ok", labels: listed }),
  create: async request => (calls.push(`create ${request.name}`), done),
  update: async request => (calls.push(`update ${request.id} ${request.name ?? ""}`), done),
  move: async request => (calls.push(`move ${request.id} ${request.parent} ${request.index}`), done),
  delete: async request => (calls.push(`delete ${request.id}`), done),
  assign: async request => (calls.push(`assign ${request.projectId} +${request.add?.join(",")} -${request.remove?.join(",")}`), done),
});
hub.connect("12345678-1234-1234-1234-123456789abc");
const projects = [{ id: "alpha", name: "alpha", path: "/alpha", archived: false }, { id: "beta", name: "beta", path: "/beta", archived: false }];

createRoot(document.getElementById("root")!).render(<div className="ide-shell settings-dialog-content" style={{ width: 1000 }}>
  <LabelSettings hub={hub} spaces={[]} projects={projects} shownId="default" canEdit />
</div>);
