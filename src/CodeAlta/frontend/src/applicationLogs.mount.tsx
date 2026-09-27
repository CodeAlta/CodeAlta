import { useState } from "react";
import { createRoot } from "react-dom/client";
import type { ApplicationLogsResponse, ApplicationLogsClearRequest, ApplicationLogsClearResponse } from "#neoastra";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";
import { createApplicationLogClearActions } from "./applicationLogClear";
import { InventoryLanguageFixture } from "./inventoryLanguage.mount";

const pending: Array<{ resolve: (value: ApplicationLogsResponse) => void; reject: (reason: Error) => void }> = [];
const pendingClear: Array<{ resolve: (value: ApplicationLogsClearResponse) => void; reject: (reason: Error) => void }> = [];
const clearRequests: ApplicationLogsClearRequest[] = [];
let reads = 0;
Object.assign(window, { logsFixture: {
  get reads() { return reads; },
  clearRequests,
  resolve(value: ApplicationLogsResponse) { pending.shift()?.resolve(Object.assign({
    captureId: value.status === "ok" ? "11111111-1111-4111-8111-111111111111" : null,
    boundary: String(reads), grant: `00000000-0000-4000-8000-${String(reads).padStart(12, "0")}`,
  }, value)); },
  reject(message: string) { pending.shift()?.reject(new Error(message)); },
  resolveClear(value: ApplicationLogsClearResponse) { pendingClear.shift()?.resolve(value); },
  rejectClear(message: string) { pendingClear.shift()?.reject(new Error(message)); },
} });
function App() {
  const [view, setView] = useState(false);
  const [actions] = useState(() => createApplicationLogClearActions(request => {
    clearRequests.push(request);
    return new Promise((resolve, reject) => pendingClear.push({ resolve, reject }));
  }));
  return <><button type="button" id="navigate-logs" onClick={() => setView(!view)}>{view ? "Settings" : "Application Logs"}</button>
    {view && <ApplicationLogsPanel clearActions={actions} read={() => { reads++; return new Promise((resolve, reject) => pending.push({ resolve, reject })); }} />}</>;
}
createRoot(document.getElementById("app")!).render(<InventoryLanguageFixture><App /></InventoryLanguageFixture>);
