import { useCallback, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Actions, Layout, Model, type ILayoutApi } from "flexlayout-react";
import "flexlayout-react/style/light.css";

// App-specific presentation. Width preferences, gesture handling and all content
// owners remain in App; this private model only projects their current geometry.
export function SessionContentLayout({ sessions, content, splitter, sessionWidth, narrow, sessionsHidden }: {
  sessions: ReactNode; content: ReactNode; splitter: ReactNode;
  sessionWidth: number; narrow: boolean; sessionsHidden: boolean;
}) {
  const layout = useRef<ILayoutApi>(null);
  const topSize = useRef<HTMLDivElement>(null);
  const pendingRedraw = useRef<number | undefined>(undefined);
  const [topHeight, setTopHeight] = useState(0);
  const redraw = useCallback(() => {
    if (pendingRedraw.current !== undefined) return;
    pendingRedraw.current = requestAnimationFrame(() => {
      pendingRedraw.current = undefined;
      layout.current?.redraw();
    });
  }, []);
  const [model] = useState(() => Model.fromJson({
    global: {
      enableEdgeDock: false, enableEdgeDockIndicators: false,
      tabEnableClose: false, tabEnableDrag: false, tabEnableRename: false,
      tabEnableFloat: false, tabEnableFloatIcon: false, tabEnablePin: false,
      tabEnablePopout: false, tabEnablePopoutIcon: false, tabEnablePopoutOverlay: false,
      tabEnableRenderOnDemand: false, tabEnableScrollbars: false,
      tabMinWidth: 0, tabMinHeight: 0, tabSetMinWidth: 0, tabSetMinHeight: 0,
      tabSetEnableClose: false, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: false,
      tabSetEnableDivide: false, tabSetEnableDrag: false, tabSetEnableDrop: false,
      tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabStrip: false,
    }, borders: [], layout: { type: "row", id: "session-content-row", children: [
      { type: "tabset", id: "session-content-rail", weight: 50, selected: 0, children: [
        { type: "tab", id: "session-content-sessions", name: "Sessions", component: "sessions", contentClassName: "session-content-rail-panel" },
      ] },
      { type: "tabset", id: "session-content-main", weight: 50, selected: 0, children: [
        { type: "tab", id: "session-content-content", name: "Content", component: "content", contentClassName: "session-content-main-panel" },
      ] },
    ] },
  }));
  useLayoutEffect(() => {
    // Measure our own CSS viewport-height rule, not package DOM. This is needed
    // for the existing 875px/600px mobile rows; it never persists a preference.
    const probe = topSize.current!;
    const measure = () => setTopHeight(probe.getBoundingClientRect().height);
    const observer = new ResizeObserver(measure);
    observer.observe(probe); measure();
    const tabs = [model.getNodeById("session-content-sessions")!, model.getNodeById("session-content-content")!];
    for (const tab of tabs) tab.setEventListener("resize", redraw);
    return () => {
      observer.disconnect();
      for (const tab of tabs) tab.removeEventListener("resize");
      if (pendingRedraw.current !== undefined) cancelAnimationFrame(pendingRedraw.current);
      pendingRedraw.current = undefined;
    };
  }, [model, redraw]);
  useLayoutEffect(() => {
    model.doAction(Actions.updateModelAttributes({ rootOrientationVertical: narrow }));
    const height = sessionsHidden ? 0 : topHeight;
    model.doAction(Actions.updateNodeAttributes("session-content-rail", {
      minWidth: narrow ? 0 : sessionWidth + 8, maxWidth: narrow ? 99999 : sessionWidth + 8,
      minHeight: narrow ? height : 0, maxHeight: narrow ? height : 99999,
    }));
    model.doAction(Actions.updateNodeAttributes("session-content-main", { minWidth: narrow ? 0 : 480 }));
    redraw();
  }, [model, narrow, sessionsHidden, sessionWidth, topHeight, redraw]);
  return <div className={`workspace-layout session-content-layout${sessionsHidden ? " sessions-hidden" : ""}`}>
    <div ref={topSize} className="session-content-top-size" aria-hidden="true" />
    <Layout ref={layout} model={model} supportsPopout={false} invalidateTabContentOnParentRender={true}
      onAction={() => undefined}
      classNameMapper={name => name === "flexlayout__splitter" ? "session-content-library-divider" : name}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      factory={node => node.getId() === "session-content-sessions"
        ? <div className="session-content-rail-slot">{sessions}{splitter}</div>
        : node.getId() === "session-content-content" ? content : null} />
  </div>;
}
