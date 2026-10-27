// Disposable page: Settings > Application Logs over a host that answers at once, with rows of several features.
// Driven by applicationLogs.browser.test.ts through `logsFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { ApplicationLogLevelsResponse, ApplicationLogLevelsSaveRequest, ApplicationLogsResponse } from "#neoastra";
import { ApplicationLogsPanel } from "./ApplicationLogsPanel";
import { createApplicationLogClearActions } from "./applicationLogClear";
import { ShellLanguageContext } from "./shellLanguage";

document.documentElement.classList.add("bp6-dark");

const row = (level: string, logger: string, feature: string, text: string) => ({ timestamp: "2026-10-27T10:00:00.000Z", level, logger, text, textTruncated: false, feature });
const logs: ApplicationLogsResponse = { status: "ok", captureOmitted: "0", readOmitted: 0, captureId: "11111111-1111-4111-8111-111111111111", boundary: "4",
  grant: "22222222-2222-4222-8222-222222222222", rows: [
    row("Info", "CodeAlta.Desktop", "system", "Startup: window at 120 ms"),
    row("Debug", "CodeAlta.Issues.Git", "issues", "GET https://dev.azure.com/org/proj/_apis/wit/wiql answered 203"),
    row("Warn", "CodeAlta.Issues", "issues", "Azure DevOps org/proj/app: listing the issues shows: Azure DevOps asks to sign in."),
    row("Error", "CodeAlta.Agent.OpenAI", "providers", "The request failed"),
  ] };
const features = [["system", "System"], ["providers", "Providers"], ["issues", "Issue trackers"], ["git", "Git"]] as const;
const held = { level: "info", features: new Map<string, string>() };
const state = { saved: [] as ApplicationLogLevelsSaveRequest[] };
const levels = (): ApplicationLogLevelsResponse => ({ status: "ok", level: held.level, editable: true, levels: ["trace", "debug", "info", "warn", "error"],
  features: features.map(([id, name]) => ({ id, name, level: held.features.get(id) ?? null })) });
const levelsApi = {
  levels: async () => levels(),
  saveLevels: async (request: ApplicationLogLevelsSaveRequest) => {
    state.saved.push(request);
    held.level = request.level;
    held.features = new Map((request.features ?? []).filter(item => item.level).map(item => [item.id, item.level!]));
    return levels();
  },
};
const clearActions = createApplicationLogClearActions(() => new Promise(() => {}));
Object.assign(window, { logsFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box" }}>
      <ApplicationLogsPanel read={async () => logs} clearActions={clearActions} levelsApi={levelsApi} epoch="epoch" />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
