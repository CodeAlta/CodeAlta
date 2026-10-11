import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { StorageSettings, type StorageMaintenanceSummary, type StorageRetentionSummary } from "./StorageSettings";
import type { StorageApi, StoragePreferences } from "./storage";
import type { SettingsFilesApi } from "../settingsFiles";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import type { StorageScanResponse } from "#neoastra";
import "../charts/charts.css";

type ScanUpdate = Pick<StorageScanResponse, "status" | "snapshot"> & Partial<StorageScanResponse>;
const state = {
  settings: { clearScratchpadOnStartup: true, retentionEnabled: true, retentionAmount: 3, retentionUnit: "months" } as StoragePreferences,
  saves: [] as StoragePreferences[], scans: [] as { signal?: AbortSignal; timeoutMilliseconds?: number; resolve: (value: ScanUpdate) => void; finish: () => void }[],
  saveStatus: "ok", readStatus: "ok", opened: 0,
};
// A realistic Windows profile: the paths are long enough to be cut, never to widen the page.
const root = "C:\\Users\\alexandre.mutel.contoso\\AppData\\Roaming\\..\\..\\.alta";
const api: StorageApi = {
  readSettings: async () => ({ status: state.readStatus, rootPath: root, settings: { ...state.settings }, error: state.readStatus === "ok" ? null : "fixture invalid config" }),
  saveSettings: async request => {
    if (!request.settings) throw new Error("fixture requires settings");
    state.saves.push(request.settings);
    if (state.saveStatus === "ok") state.settings = { ...request.settings };
    return { status: state.saveStatus, rootPath: root, settings: { ...state.settings }, error: state.saveStatus === "ok" ? null : "fixture write failure" };
  },
  scan: async (_request, options) => {
    const updates: StorageScanResponse[] = [];
    let wake: (() => void) | undefined;
    let finished = options?.signal?.aborted ?? false;
    const finish = () => { finished = true; wake?.(); };
    options?.signal?.addEventListener("abort", finish, { once: true });
    state.scans.push({ ...options, resolve: value => { updates.push({ processedFiles: "0", processedDirectories: "0", ...value }); wake?.(); }, finish });
    return (async function* () {
      try {
        while (!finished) {
          if (!updates.length) await new Promise<void>(resolve => { wake = resolve; });
          while (!finished && updates.length) {
            const update = updates.shift()!;
            yield update;
            if (update.status !== "running") return;
          }
        }
      } finally { options?.signal?.removeEventListener("abort", finish); }
    })();
  },
};
const filesApi: SettingsFilesApi = {
  list: async () => ({ status: "ok", platform: "windows", locations: [{ kind: "config", scope: "Global", id: null, path: `${root}\\config.toml`, folder: false, exists: true, canOpen: true, readOnly: false }] }),
  open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }),
};
// The Settings window's content area: 518 x 484 is what a 900 x 650 main window gives it.
function Fixture() {
  const [shown, setShown] = useState(true);
  const [epoch, setEpoch] = useState<string | null>("fixture");
  const [locale, setLocale] = useState<Locale>("en");
  const [size, setSize] = useState({ width: 518, height: 484 });
  const [maintenance, setMaintenance] = useState<StorageMaintenanceSummary | undefined>({ state: "partial", reasons: ["Fixture: locked scratchpad item was left in place."] });
  const [retention, setRetention] = useState<StorageRetentionSummary | undefined>({ state: "skipped", examined: 0, deleted: 0, skipped: 0, reason: "external_admission_unqualified" });
  Object.assign(window, { storageFixture: { state, setShown, setEpoch, setLocale, setSize, setMaintenance, setRetention } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="settings-dialog-content" style={{ ...size, overflow: "auto" }}>
      {shown && <StorageSettings epoch={epoch} api={api} filesApi={filesApi} onOpenFile={() => state.opened++} maintenance={maintenance} retention={retention} />}
    </div>
  </ShellLanguageContext.Provider>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
