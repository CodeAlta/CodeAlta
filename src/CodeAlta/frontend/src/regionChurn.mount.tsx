// Disposable page: what moves in the regions of the window (a tab of the title bar, a popover, a toast), drawn by the
// production components, as the test measures it.
import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { ActivitySpinner } from "./ActivitySpinner";
import { BackgroundMark } from "./BackgroundTaskViews";
import { PaintedProgressBar } from "./PaintedProgressBar";
import { PluginButtons } from "./pluginButtons/PluginButtons";
import { PluginButtonsContext, createHiddenButtons, type PluginButtonsHost } from "./pluginButtons/pluginButtonModel";
import { dragRegionRules } from "./regionChurn";
import { LevelBar } from "./speech/MicrophoneChoice";
import type { Speech } from "./speech/speech";

const sheet = new CSSStyleSheet();
sheet.replaceSync(dragRegionRules);
document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];

// A microphone that hears ten levels a second, as the setup of dictation shows them.
const speech = { subscribeLevel: (listener: (level: number, source: string) => void) => {
  const timer = setInterval(() => listener(Math.random(), "setup"), 100);
  return () => clearInterval(timer);
} } as unknown as Speech;

/** A download that goes on: its bar is told where it is five times a second. */
function Download() {
  const [value, setValue] = useState(0);
  useEffect(() => { const timer = setInterval(() => setValue(current => (current + 0.013) % 1), 200); return () => clearInterval(timer); }, []);
  return <PaintedProgressBar intent="primary" stripes={false} animate={false} value={value} />;
}

// A plugin whose button of the rail is busy: its ring turns there, in the drag region of the title bar.
const memory = new Map<string, string>();
const plugins: PluginButtonsHost = {
  epoch: "epoch-1", spaceId: "work", hidden: createHiddenButtons({ getItem: key => memory.get(key) ?? null, setItem: (key, value) => { memory.set(key, value); } }),
  api: { buttons: async request => ({ status: "ok", place: request.place, buttons: request.place === "Rail" ? [{ id: "builtin:fixture/sync", pluginKey: "builtin:fixture", pluginId: "fixture",
    plugin: "Fixture", buttonId: "sync", place: "Rail", icon: "refresh-cw", iconData: null, label: "Sync", commandId: "command-1", canvas: null, canvasScope: null, badge: "busy", count: 0,
    tone: "Info", hidden: false, disabled: false, tooltip: null }] : [] }) as never },
  activate: () => { }, isActive: () => false,
};

createRoot(document.getElementById("root")!).render(<StrictMode><PluginButtonsContext.Provider value={plugins}><div className="ide-shell" style={{ height: "100%" }}>
  <div className="window-brand" data-neoastra-drag-region="true"><nav className="activity-rail" aria-label="Workspace navigation">
    <PluginButtons place="Rail" context={{ projectId: null, sessionId: null }} />
  </nav></div>
  <div className="region-tabs" data-neoastra-drag-region="true" style={{ display: "flex", height: 36, padding: "0 8px", alignItems: "flex-end" }}>
    <button type="button" id="tab" className="flexlayout__tab_button" style={{ display: "inline-flex", alignItems: "center", height: 30 }}>
      <ActivitySpinner size={12} /><BackgroundMark count={2} /><span>A session</span>
    </button>
  </div>
  <div className="bp6-overlay-content" style={{ position: "absolute", left: 40, top: 80, width: 260, padding: 12 }}><LevelBar speech={speech} source="setup" /></div>
  <div className="bp6-toast" style={{ position: "absolute", left: 40, top: 160, width: 320, padding: 10 }}><Download /></div>
</div></PluginButtonsContext.Provider></StrictMode>);
Object.assign(window, { regionFixture: {} });
