// Disposable page: Move to machine… with a scripted host that lists two machines online. Driven by
// moveToMachine.browser.test.ts through `moveToMachineFixture`: what the dialog asks is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { MachineMovePreview, MachineView } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { MoveToMachineDialog } from "./MoveToMachineDialog";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const machine = (id: string, name: string): MachineView => ({ id, name, state: "online", os: "linux", version: "4.0.0", address: null, fingerprint: "ab cd",
  words: "a b c d e f", sessions: 0, lastSeen: null, problem: null, permissions: ["start", "move"], pairedAt: "2026-11-01T00:00:00Z", paired: true, channel: "direct" });
const preview = (target: string | null): MachineMovePreview => ({ status: "ok", message: null, busy: null, jobs: [], secrets: [], unpushedCommits: 0, dirty: false,
  branch: "main", repositoryKey: "github.com/xoofx/markdig", targetStatus: "accepted", targetCode: null, targetMessage: null, projectName: "markdig",
  projectPath: `/home/me/${target ?? "here"}/markdig`, canClone: false, targetWarnings: [], receiveMoves: true, machineName: "desktop" });
const api = {
  list: async () => { calls.push("list"); return { status: "ok", hostEpoch: "epoch", machines: [machine("pc-ai-id", "pc-ai"), machine("mac-id", "mac")] }; },
  moveSettings: async () => ({ status: "ok", receiveMoves: true, machineName: "desktop" }),
  setReceiveMoves: async () => ({ status: "ok", receiveMoves: true, machineName: "desktop" }),
  movePreview: async (request: { targetMachineId: string | null }) => { calls.push(`preview ${request.targetMachineId}`); return preview(request.targetMachineId); },
  moveSession: async () => ({ status: "finished", code: null, message: null, sessionId: "s1", machineId: null, workFolder: null, warnings: [] }),
};
Object.assign(window, { moveToMachineFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <MoveToMachineDialog epoch="epoch" session={{ id: "s1", title: "Fix the parser", machineId: null, machineName: null }}
      api={api as unknown as Parameters<typeof MoveToMachineDialog>[0]["api"]} onClose={() => calls.push("close")} onMoved={() => calls.push("moved")} />
  </ShellLanguageContext.Provider>
</StrictMode>);
