import { useState, type ReactNode } from "react";
import { Layout, Model } from "flexlayout-react";
import "flexlayout-react/style/light.css";

// Presentation only: this private model must never become a session inventory or action owner.
export function WorkspaceLayout({ children }: { children: ReactNode }) {
  const [model] = useState(() => Model.fromJson({
    global: {
      enableEdgeDock: false, enableEdgeDockIndicators: false,
      tabEnableClose: false, tabEnableDrag: false, tabEnableRename: false,
      tabEnableFloat: false, tabEnableFloatIcon: false, tabEnablePin: false,
      tabEnablePopout: false, tabEnablePopoutIcon: false, tabEnablePopoutOverlay: false,
      tabEnableRenderOnDemand: false, tabEnableScrollbars: false,
      tabSetEnableClose: false, tabSetEnableCloseButton: false, tabSetEnableDeleteWhenEmpty: false,
      tabSetEnableDivide: false, tabSetEnableDrag: false, tabSetEnableDrop: false,
      tabSetEnableMaximize: false, tabSetEnableActiveIcon: false, tabSetEnableTabStrip: false,
    },
    borders: [],
    layout: { type: "row", id: "workspace-row", children: [
      { type: "tabset", id: "workspace-panel", selected: 0, children: [
        { type: "tab", id: "workspace-content", name: "Workspace", component: "workspace" },
      ] },
    ] },
  }));
  return <div className="workspace-layout">
    <Layout model={model} supportsPopout={false} invalidateTabContentOnParentRender={true}
      onAction={() => undefined}
      keyMap={{ closeTab: undefined, renameTab: undefined, focusTabToggle: undefined,
        focusNextTabset: undefined, focusPreviousTabset: undefined, closeOverlayBorder: undefined }}
      factory={node => node.getId() === "workspace-content" && node.getComponent() === "workspace" ? children : null} />
  </div>;
}
