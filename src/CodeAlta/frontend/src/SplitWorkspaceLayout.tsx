import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Actions, Layout, Model, type ILayoutApi } from "flexlayout-react";
import "flexlayout-react/style/light.css";

// Opt-in presentation only. Neither this private model nor its IDs represent sessions.
export function SplitWorkspaceLayout({ left, right }: { left: ReactNode; right: ReactNode }) {
  const layout = useRef<ILayoutApi>(null);
  const pendingRedraw = useRef<number | undefined>(undefined);
  const scheduleRedraw = useCallback(() => {
    if (pendingRedraw.current !== undefined) return;
    // FlexLayout computes separator ARIA from geometry established during its render.
    // Request a public follow-up render after weight changes; this is not a commit barrier.
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
      tabEnableRenderOnDemand: false, tabEnableScrollbars: false, tabMinWidth: 160,
      tabSetEnableClose: false, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: false,
      tabSetEnableDivide: false, tabSetEnableDrag: false, tabSetEnableDrop: false,
      tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabStrip: false,
      tabSetMinWidth: 160,
    },
    borders: [],
    layout: { type: "row", id: "split-workspace-row", children: [
      { type: "tabset", id: "split-workspace-left", weight: 50, selected: 0, children: [
        { type: "tab", id: "split-left-content", name: "Left", component: "workspace" },
      ] },
      { type: "tabset", id: "split-workspace-right", weight: 50, selected: 0, children: [
        { type: "tab", id: "split-right-content", name: "Right", component: "workspace" },
      ] },
    ] },
  }));
  useEffect(() => {
    // Container-only resizes can update geometry without refreshing separator ARIA.
    // Use the library's public measured-tab events, not another ResizeObserver.
    const tabs = [model.getNodeById("split-left-content")!, model.getNodeById("split-right-content")!];
    for (const tab of tabs) tab.setEventListener("resize", scheduleRedraw);
    return () => {
      for (const tab of tabs) tab.removeEventListener("resize");
      if (pendingRedraw.current !== undefined) cancelAnimationFrame(pendingRedraw.current);
      pendingRedraw.current = undefined;
    };
  }, [model, scheduleRedraw]);
  return <div className="workspace-layout">
    <Layout ref={layout} model={model} supportsPopout={false} realtimeResize={false}
      invalidateTabContentOnParentRender={true}
      onAction={action => action.type === Actions.ADJUST_WEIGHTS && action.data.nodeId === "split-workspace-row" ? action : undefined}
      onModelChange={(_model, action) => {
        if (action.type === Actions.ADJUST_WEIGHTS && action.data.nodeId === "split-workspace-row") scheduleRedraw();
      }}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      factory={node => node.getComponent() !== "workspace" ? null
        : node.getId() === "split-left-content" ? left : node.getId() === "split-right-content" ? right : null} />
  </div>;
}
