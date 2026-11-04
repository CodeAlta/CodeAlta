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

// A plugin whose button of the rail is busy: its ring turns there, in the drag region of the title bar. Its buttons come
// late, as from a host under load: the rail grows once they come, which a measure that starts before is not to blame on
// what it measures.
const memory = new Map<string, string>();
const plugins: PluginButtonsHost = {
  epoch: "epoch-1", spaceId: "work", hidden: createHiddenButtons({ getItem: key => memory.get(key) ?? null, setItem: (key, value) => { memory.set(key, value); } }),
  api: { buttons: async request => (await new Promise(resolve => setTimeout(resolve, 400)), { status: "ok", place: request.place, buttons: request.place === "Rail" ? [{ id: "builtin:fixture/sync", pluginKey: "builtin:fixture", pluginId: "fixture",
    plugin: "Fixture", buttonId: "sync", place: "Rail", icon: "refresh-cw", iconData: null, label: "Sync", commandId: "command-1", canvas: null, canvasScope: null, badge: "busy", count: 0,
    tone: "Info", hidden: false, disabled: false, tooltip: null }] : [] }) as never },
  activate: () => { }, isActive: () => false,
};

createRoot(document.getElementById("root")!).render(<StrictMode><PluginButtonsContext.Provider value={plugins}><div className="ide-shell" style={{ height: "100%" }}>
  <div className="window-brand" data-neoastra-drag-region="true"><div className="activity-rail-room"><nav className="activity-rail" aria-label="Workspace navigation">
    <PluginButtons place="Rail" context={{ projectId: null, sessionId: null }} />
  </nav></div></div>
  <div className="region-tabs" data-neoastra-drag-region="true" style={{ display: "flex", height: 36, padding: "0 8px", alignItems: "flex-end" }}>
    <button type="button" id="tab" className="flexlayout__tab_button" style={{ display: "inline-flex", alignItems: "center", height: 30 }}>
      <ActivitySpinner size={12} /><BackgroundMark count={2} /><span>A session</span>
    </button>
  </div>
  {/* The tab strip of the title bar with more tabs than it shows, some scrolled out of it. */}
  <div id="tab-strip" className="flexlayout__tabset_tabbar_outer flexlayout__tabset_tabbar_outer_top" data-neoastra-drag-region="true" style={{ display: "flex", width: 700 }}>
    <div className="flexlayout__mini_scrollbar_container">
      <div className="flexlayout__tabset_tabbar_inner flexlayout__tabset_tabbar_inner_top">
        <div className="flexlayout__tabset_tabbar_inner_tab_container flexlayout__tabset_tabbar_inner_tab_container_top" style={{ display: "flex", width: 2400 }}>
          {Array.from({ length: 12 }, (_, index) => <div key={index} className="flexlayout__tab_button flexlayout__tab_button_top" role="button">
            <span className="flexlayout__tab_button_content">Session {index}</span><button type="button" className="flexlayout__tab_button_trailing">x</button>
            <div className="flexlayout__tabset_tab_spacer flexlayout__tabset_tab_divider" /></div>)}
        </div>
      </div>
    </div>
    <div className="flexlayout__tab_toolbar"><button type="button" className="flexlayout__tab_toolbar_button">+</button></div>
  </div>
  <div className="bp6-overlay-content" style={{ position: "absolute", left: 40, top: 120, width: 260, padding: 12 }}><div id="popover-body"><LevelBar speech={speech} source="setup" /></div></div>
  <dialog open id="region-dialog" style={{ position: "absolute", left: 340, top: 120, width: 200 }}><p id="dialog-body">A dialog</p></dialog>
  <div className="bp6-toast" style={{ position: "absolute", left: 40, top: 200, width: 320, padding: 10 }}><Download /></div>
</div></PluginButtonsContext.Provider></StrictMode>);
Object.assign(window, { regionFixture: {} });
