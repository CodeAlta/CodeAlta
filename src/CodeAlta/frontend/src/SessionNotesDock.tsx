import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Actions, DockLocation, Layout, Model, TabNode } from "flexlayout-react";
import { NotesPanel } from "./NotesPanel";
import type { createNotesReader } from "./sessionNotes";
import type { createMutationCapability } from "./sessionOperations";
import { useShellLanguage } from "./shellLanguage";

export function SessionNotesDock({ sessionId, epoch, capability, fallbackMarkdown, toggle, children, reader, onActivate }: {
  reader: ReturnType<typeof createNotesReader>; onActivate?: () => void;
  sessionId: string; epoch?: string; capability?: ReturnType<typeof createMutationCapability>;
  fallbackMarkdown: string; toggle?: boolean; children: ReactNode;
}) {
  const { t, locale } = useShellLanguage();
  const [model] = useState(() => Model.fromJson({ global: {
    tabEnableRename: false, tabEnableClose: false, tabEnableRenderOnDemand: false,
    tabEnableFloat: false, tabEnablePopout: false, tabSetEnableMaximize: false, tabSetEnableDrag: false,
  }, borders: [{ type: "border", location: "right", size: 280, selected: -1, children: [
    { type: "tab", id: "notes", name: t("Alta notes"), component: "notes", enableDrag: true },
  ] }, { type: "border", location: "left", size: 280, selected: -1, children: [] }],
  layout: { type: "row", children: [{ type: "tabset", enableTabStrip: false, enableDrop: false, enableDeleteWhenEmpty: false, children: [
    { type: "tab", id: "conversation", name: t("Session timeline"), component: "conversation", enableDrag: false },
  ] }] } }));
  const hadContent = useRef(false);
  const observedToggle = useRef(toggle);
  const showContent = useCallback((markdown: string) => {
    const nonempty = !!markdown.trim();
    const node = model.getNodeById("notes");
    if (nonempty && !hadContent.current && node instanceof TabNode && !node.isSelected()) model.doAction(Actions.selectTab("notes"));
    if (!nonempty && hadContent.current && node instanceof TabNode && node.isSelected() && node.getParent()?.getType() === "border")
      model.doAction(Actions.selectTab("notes"));
    hadContent.current = nonempty;
  }, [model]);
  useEffect(() => {
    if (toggle !== undefined && observedToggle.current !== undefined && toggle !== observedToggle.current)
      model.doAction(Actions.selectTab("notes"));
    observedToggle.current = toggle;
  }, [toggle, model]);
  useEffect(() => {
    model.doAction(Actions.renameTab("notes", t("Alta notes")));
    model.doAction(Actions.renameTab("conversation", t("Session timeline")));
  }, [locale, model]);
  return <div className="session-notes-dock workspace-layout" onFocusCapture={onActivate} onPointerDownCapture={onActivate}><Layout model={model} supportsPopout={false}
    invalidateTabContentOnParentRender={true} factory={node => node.getId() === "conversation" ? children
      : <NotesPanel epoch={epoch} sessionId={sessionId} reader={reader} capability={capability}
          fallbackMarkdown={fallbackMarkdown} docked onContent={showContent} preferredHeight={400}
          onResize={() => {}} onReset={() => {}} onCleared={() => showContent("")}
          onClose={() => {
            const node = model.getNodeById("notes");
            const parent = node?.getParent();
            if (parent?.getType() === "border") model.doAction(Actions.selectTab("notes"));
            else if (node) model.doAction(Actions.moveNode("notes", "border_right", DockLocation.CENTER, -1, false));
          }} />} /></div>;
}
