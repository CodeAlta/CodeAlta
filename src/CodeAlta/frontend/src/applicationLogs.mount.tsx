import { useState } from "react";
import { createRoot } from "react-dom/client";
import type { ApplicationLogsResponse } from "#neoastra";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";

const pending: Array<{ resolve: (value: ApplicationLogsResponse) => void; reject: (reason: Error) => void }> = [];
let reads = 0;
Object.assign(window, { logsFixture: {
  get reads() { return reads; },
  resolve(value: ApplicationLogsResponse) { pending.shift()?.resolve(value); },
  reject(message: string) { pending.shift()?.reject(new Error(message)); },
} });
function App() {
  const [view, setView] = useState(false);
  return <><button type="button" id="navigate-logs" onClick={() => setView(!view)}>{view ? "Settings" : "Application Logs"}</button>
    {view && <ApplicationLogsPanel read={() => { reads++; return new Promise((resolve, reject) => pending.push({ resolve, reject })); }} />}</>;
}
createRoot(document.getElementById("app")!).render(<App />);
