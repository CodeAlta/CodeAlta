import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { fileAppearance } from "../fileAppearance";
import { useShellLanguage } from "../shellLanguage";
import { editorTabNames, type EditorFile } from "./editorWorkbench";

/** What is known of an open file beyond its path, for its tab. */
export type EditorTabState = Readonly<{ dirty: boolean; missing: boolean }>;
const dragType = "application/x-codealta-editor-tab";

/**
 * The tabs of the files open in a project's code editor. A previewed file is in italics until it is kept open
 * (a double click, or an edit); a file with unsaved edits shows a dot where its close button is. Tabs are
 * dragged to another place of the strip, and closed with the middle button.
 */
export function EditorTabs({ files, active, state, tooltip, leading, trailing, onSelect, onClose, onPin, onMove, onMenu }: {
  files: readonly EditorFile[]; active: string | null;
  state: (path: string) => EditorTabState;
  /** What the tooltip of a tab says: where its file is on the disk. Without it, the path of the file in its folder. */
  tooltip?: (path: string) => string;
  /** Controls before and after the tabs: the side toggle, the actions of the file shown. */
  leading?: ReactNode; trailing?: ReactNode;
  onSelect: (path: string) => void; onClose: (path: string) => void; onPin: (path: string) => void;
  onMove: (path: string, index: number) => void;
  onMenu: (path: string, point: Readonly<{ x: number; y: number }>) => void;
}) {
  const { t } = useShellLanguage();
  const strip = useRef<HTMLDivElement>(null);
  const [drop, setDrop] = useState<number | null>(null);
  const names = editorTabNames(files);
  // The tab of the file shown stays in view: when it changes, and when the strip gets narrower.
  useLayoutEffect(() => {
    const node = strip.current;
    if (!node) return;
    const show = () => {
      const tab = node.querySelector<HTMLElement>('[aria-selected="true"]');
      if (!tab) return;
      if (tab.offsetLeft < node.scrollLeft) node.scrollLeft = tab.offsetLeft;
      else if (tab.offsetLeft + tab.offsetWidth > node.scrollLeft + node.clientWidth) node.scrollLeft = tab.offsetLeft + tab.offsetWidth - node.clientWidth;
    };
    show();
    const resized = new ResizeObserver(show);
    resized.observe(node);
    return () => resized.disconnect();
  }, [active, files.length]);
  // Where a dragged tab would land: before the tab whose first half the pointer is over.
  function target(clientX: number) {
    const tabs = Array.from(strip.current?.querySelectorAll<HTMLElement>('[role="tab"]') ?? []);
    const index = tabs.findIndex(tab => { const box = tab.getBoundingClientRect(); return clientX < box.left + box.width / 2; });
    return index < 0 ? tabs.length : index;
  }
  return <div className="editor-tabs">
    {leading}
    <div ref={strip} className="editor-tab-strip" role="tablist" aria-label={t("Open files")}
      onWheel={event => { if (event.deltaY !== 0 && !event.shiftKey) event.currentTarget.scrollLeft += event.deltaY; }}
      onDragOver={event => { if (event.dataTransfer.types.includes(dragType)) { event.preventDefault(); event.dataTransfer.dropEffect = "move"; setDrop(target(event.clientX)); } }}
      onDragLeave={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setDrop(null); }}
      onDrop={event => {
        const path = event.dataTransfer.getData(dragType);
        setDrop(null);
        if (!path) return;
        event.preventDefault();
        const from = files.findIndex(file => file.path === path), to = target(event.clientX);
        // Past its own place, the tab lands one earlier once it has left that place.
        if (from >= 0) onMove(path, to > from ? to - 1 : to);
      }}>
      {files.map((file, index) => {
        const look = fileAppearance(file.path, false), shown = names.get(file.path)!, known = state(file.path);
        return <div key={file.path} role="tab" id={`editor-tab-${index}`} className="editor-tab" aria-selected={file.path === active} tabIndex={-1} draggable
          data-preview={file.preview} data-dirty={known.dirty} data-missing={known.missing} data-drop={drop === index ? "before" : drop === files.length && index === files.length - 1 ? "after" : undefined}
          title={tooltip ? tooltip(file.path) : file.path}
          onDragStart={event => { event.dataTransfer.setData(dragType, file.path); event.dataTransfer.effectAllowed = "move"; }}
          onDragEnd={() => setDrop(null)}
          onMouseDown={event => { if (event.button === 1) event.preventDefault(); }}
          onClick={() => onSelect(file.path)} onDoubleClick={() => onPin(file.path)}
          onAuxClick={event => { if (event.button === 1) { event.preventDefault(); onClose(file.path); } }}
          onContextMenu={event => { event.preventDefault(); onMenu(file.path, { x: event.clientX, y: event.clientY }); }}>
          <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
          <span className="editor-tab-name">{shown.name}</span>
          {shown.folder && <span className="editor-tab-folder">{shown.folder}</span>}
          <button type="button" className="editor-tab-close" tabIndex={-1} aria-label={t("Close {name}", { name: shown.name })} title={known.dirty ? t("Unsaved changes") : t("Close")}
            onClick={event => { event.stopPropagation(); onClose(file.path); }} onDoubleClick={event => event.stopPropagation()}>
            <span className="editor-tab-dot" aria-hidden="true" /><AppIcon name="close" size={13} />
          </button>
        </div>;
      })}
    </div>
    {trailing}
  </div>;
}
