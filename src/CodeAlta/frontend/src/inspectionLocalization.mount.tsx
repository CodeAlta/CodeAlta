// Disposable presentation harness: real controls/context and existing fake RPC bridge.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { ShellLanguageContext } from "./shellLanguage";
import type { Locale } from "./localization";
import { SessionInfoDialog } from "./SessionInfoDialog";
import { SessionUsageInspector } from "./SessionUsageInspector";
import { SessionBrowser } from "./SavedSessionBrowser";
import { createMutationCapability } from "./sessionOperations";
import type { RuntimeTarget } from "./runtimeObservations";
import { sessionInfoView } from "./sessionInfo";
import type { WorkspaceSnapshot } from "#neoastra";

const epoch = "12345678-1234-1234-1234-123456789abc";
const snapshot: WorkspaceSnapshot = { configured: true, projects: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false,
  sessions: [{ id: "Unknown", title: "Saved metadata", fullTitle: "Saved metadata", fullTitleTruncated: false,
    scopeKind: "global", projectId: null, workspacePath: null, providerKey: "Unknown", createdAt: null,
    updatedAt: "2026-01-01T00:00:00Z", parentSessionId: null, lineageIssue: null }] };
const target: RuntimeTarget = { tab: { sessionId: "Unknown", projectId: null, path: null }, request: {
  expectedHostEpoch: epoch, sessionId: "Unknown", scope: "global", projectId: null, projectPath: null, createdAt: "2026-01-01T00:00:00Z" } };

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [view, setView] = useState("info");
  const [stale, setStale] = useState(false);
  const [capability] = useState(() => createMutationCapability(epoch));
  Object.assign(window, { inspectionFixture: { setLocale, setView, setStale, canMutate: capability.canMutate,
    invalidate: () => capability.observe({ status: "stale_epoch", epoch: "changed-host" }) } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <button id="fixture-focus">Return focus</button>
    {view === "info" && <SessionInfoDialog info={sessionInfoView(snapshot, snapshot.sessions[0], null)} demo={false} target={target} onClose={() => setView("closed")} canRead={capability.canMutate} />}
    {view === "usage" && <SessionUsageInspector target={{ epoch, sessionId: "Unknown", scope: "global", projectId: null, expectedProjectPath: null }} capability={capability} />}
    {view === "browser" && <SessionBrowser snapshot={snapshot} projectId={null} stale={stale} open={() => { setView("closed"); return true; }} close={() => setView("closed")} />}
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<Fixture />);
