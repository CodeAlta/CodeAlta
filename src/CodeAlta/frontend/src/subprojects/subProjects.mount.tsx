// The window of the sub-projects of a repository, on a host that keeps a map in memory: what the window asks of it is
// recorded for the test, and the map it answers follows the changes.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { SubProjectEntryView, SubProjectsMapView } from "#neoastra";
import { SubProjectsWindow, type SubProjectsApi } from "./SubProjectsWindow";

const calls: string[] = [];
(window as unknown as { calls: string[] }).calls = calls;
const entry = (key: string, path: string, look: Partial<SubProjectEntryView> = {}): SubProjectEntryView => ({ key, path, name: null, description: null, labels: [],
  optional: false, write: "repository", folder: "ok", submodule: false, projectId: `id-${key}`, projectName: null, hidden: false, claimedBy: null, subProjects: 0, ...look });
const map: SubProjectsMapView = {
  projectId: "repo", name: "contoso", path: "/contoso", manifestPath: "/contoso/.alta/projects.toml", exists: true, claimedBy: null,
  // contoso is itself a sub-project of platform: its map is a nested map, and billing has a map of its own.
  parentId: "platform", parentName: "platform",
  labels: [{ key: "backend", id: "contoso.backend", name: "Backend", parent: null, icon: null, color: "#2d72d2", description: null }],
  entries: [entry("billing", "services/billing", { name: "Billing", labels: ["backend"], subProjects: 2 }), entry("web", "apps/web", { hidden: true }),
    entry("reports", "tools/reports", { optional: true, projectId: null }), entry("sdk", "vendor/sdk", { submodule: true }),
    entry("legacy", "services/legacy", { folder: "missing", projectId: null })],
  problems: [{ key: "legacy", code: "folder.missing", message: "The folder `services/legacy` of `legacy` is not there." }],
};
const ok = { status: "ok", message: null, key: null };
const api: SubProjectsApi = {
  map: async () => ({ status: "ok", map }),
  scan: async request => (calls.push(`scan ${request.glob ?? ""}`), { status: "ok", truncated: false, message: null,
    candidates: [{ path: "apps/admin", key: "admin", markers: ["package.json"], submodule: false, declared: null },
      { path: "services/billing", key: "billing", markers: ["billing.csproj"], submodule: false, declared: "billing" }] }),
  show: async request => (calls.push(`show ${request.key} ${request.shown}`), ok),
  add: async request => (calls.push(`add ${request.path}`), ok),
  update: async request => (calls.push(`update ${request.key} name=${request.name} labels=${request.labels?.join(",") ?? "-"} write=${request.write ?? "-"}`), ok),
  remove: async request => (calls.push(`remove ${request.key}`), ok),
  label: async request => (calls.push(`label ${request.key} ${request.name ?? ""} remove=${request.remove}`), ok),
};

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell">
  <SubProjectsWindow epoch="12345678-1234-1234-1234-123456789abc" project={{ id: "repo", name: "contoso" }} api={api}
    onClose={() => calls.push("close")} onOpenMap={path => calls.push(`open ${path}`)} onSetUp={() => calls.push("setup")}
    onOpenProject={id => calls.push(`project ${id}`)} onChanged={() => calls.push("changed")} />
</div></StrictMode>);
