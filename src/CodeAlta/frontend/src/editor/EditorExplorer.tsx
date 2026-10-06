import { memo, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type RefObject } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { changeLabel, changeLetter, type ChangeStatus } from "../changes/projectChanges";
import { fileAppearance } from "../fileAppearance";
import { useShellLanguage } from "../shellLanguage";
import { parentTreePath, treeBaseName, treeEntryRows, treeNameProblem, treeTypeAhead, type FileTree, type TreeDecorations, type TreeEdit, type TreeRow } from "./fileTree";
import { useWindowedRows } from "./windowedRows";

export type ExplorerEntry = Extract<TreeRow, { kind: "entry" }>;
export type ExplorerHandle = Readonly<{ focus: () => void }>;
export const explorerRowHeight = 24;
const indent = (depth: number): CSSProperties => ({ paddingLeft: 6 + depth * 12, "--depth": depth } as CSSProperties);

const EntryRow = memo(function EntryRow({ row, selected, active, status, changed, onClick, onOpen, onMenu, onDragStart, onDropInto }: {
  row: ExplorerEntry; selected: boolean; active: boolean;
  /** The git status of a file, or whether a folder holds a changed file. */
  status: string | undefined; changed: boolean;
  onClick: (row: ExplorerEntry) => void; onOpen: (row: ExplorerEntry) => void;
  onMenu: (row: ExplorerEntry, point: Readonly<{ x: number; y: number }>) => void;
  onDragStart: (row: ExplorerEntry) => void; onDropInto: (folder: string) => void;
}) {
  const { t } = useShellLanguage();
  const [over, setOver] = useState(false);
  const look = row.directory ? { icon: row.expanded ? "open" as const : "folder" as const, tone: "gold" } : fileAppearance(row.path, false);
  return <div role="treeitem" className="editor-tree-row" aria-level={row.depth + 1} aria-expanded={row.directory ? row.expanded : undefined} aria-selected={selected}
    data-path={row.path} data-active={active} data-ignored={row.ignored} data-git={status} data-over={over || undefined} style={indent(row.depth)} title={row.path} draggable
    onClick={() => onClick(row)} onDoubleClick={() => onOpen(row)}
    onContextMenu={event => { event.preventDefault(); onMenu(row, { x: event.clientX, y: event.clientY }); }}
    onDragStart={event => { event.dataTransfer.setData("application/x-codealta-tree-entry", row.path); event.dataTransfer.effectAllowed = "move"; onDragStart(row); }}
    onDragOver={row.directory ? event => { if (event.dataTransfer.types.includes("application/x-codealta-tree-entry")) { event.preventDefault(); event.stopPropagation(); setOver(true); } } : undefined}
    onDragLeave={row.directory ? () => setOver(false) : undefined}
    onDrop={row.directory ? event => { event.preventDefault(); event.stopPropagation(); setOver(false); onDropInto(row.path); } : undefined}>
    <span className="editor-tree-twist">{row.directory && (row.loading ? <ActivitySpinner size={11} /> : <AppIcon name={row.expanded ? "chevronDown" : "chevronRight"} size={13} />)}</span>
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <span className="editor-tree-name">{row.name}</span>
    {status ? <span className="editor-tree-git" title={t(changeLabel(status as ChangeStatus))}>{changeLetter(status as ChangeStatus)}</span>
      : changed && <span className="editor-tree-changed" title={t("Contains changed files")} />}
  </div>;
});

// The name being typed for a new entry or for the entry being renamed, in the place the entry has in the tree.
function NameInput({ row, tree, onCommit, onCancel }: { row: Extract<TreeRow, { kind: "input" }>; tree: FileTree; onCommit: (name: string) => void; onCancel: () => void }) {
  const { t } = useShellLanguage();
  const input = useRef<HTMLInputElement>(null);
  const current = row.path === null ? null : treeBaseName(row.path);
  const [value, setValue] = useState(current ?? "");
  const done = useRef(false);
  const problem = value === (current ?? "") ? null : treeNameProblem(value, tree.folders.get(row.parent)?.entries ?? [], current, row.path === null);
  useLayoutEffect(() => {
    const node = input.current;
    if (!node) return;
    node.focus();
    // A renamed file starts with its name selected, without its extension.
    const dot = current && !row.directory ? current.lastIndexOf(".") : -1;
    node.setSelectionRange(0, dot > 0 ? dot : node.value.length);
  }, []);
  function finish(commit: boolean) {
    if (done.current) return;
    const name = value.trim();
    if (commit && (problem || !name)) { if (!name) { done.current = true; onCancel(); } return; }
    done.current = true;
    if (commit && name !== current) onCommit(name.replace(/\\/gu, "/")); else onCancel();
  }
  const look = row.directory ? { icon: "folder" as const, tone: "gold" } : fileAppearance(value || "file", false);
  return <div className="editor-tree-row editor-tree-input" style={indent(row.depth)} data-invalid={!!problem}>
    <span className="editor-tree-twist" />
    <span className="file-tab-icon" data-file-tone={look.tone}><AppIcon name={look.icon} size={14} /></span>
    <input ref={input} value={value} spellCheck={false} autoComplete="off" maxLength={512} aria-invalid={!!problem}
      aria-label={t(row.path !== null ? "New name" : row.directory ? "Name of the new folder" : "Name of the new file")}
      onChange={event => setValue(event.target.value)} onBlur={() => finish(!problem && value.trim() !== "")}
      onKeyDown={event => {
        event.stopPropagation();
        if (event.nativeEvent.isComposing) return;
        if (event.key === "Enter") { event.preventDefault(); finish(true); }
        else if (event.key === "Escape") { event.preventDefault(); done.current = true; onCancel(); }
      }} />
    {problem && <span className="editor-tree-problem" role="alert">{t(problem)}</span>}
  </div>;
}

/**
 * The files of a project as a tree. A folder shows what it holds once it is opened: nothing below a closed folder
 * was read. One click previews a file, a double click or Enter keeps it open. Arrows, Home, End and the letters
 * of a name move through the rows; F2 renames and Delete deletes the selected entry. An entry is dragged onto a
 * folder to move it there.
 */
export function EditorExplorer({ tree, rows, selected, active, decorations, label, handle, onSelect, onOpen, onToggle, onCommit, onCancel, onMenu, onAction, onMove }: {
  tree: FileTree; rows: readonly TreeRow[];
  /** The row the keyboard is on, and the file the editor shows. */
  selected: string | null; active: string | null;
  decorations: TreeDecorations; label: string;
  handle: RefObject<ExplorerHandle | null>;
  onSelect: (path: string | null) => void;
  /** Opens a file: as a preview when it was only clicked, kept open (and with the keyboard) otherwise. */
  onOpen: (path: string, keep: boolean) => void;
  onToggle: (path: string) => void;
  /** The name typed for the entry being created or renamed. */
  onCommit: (edit: TreeEdit, name: string) => void; onCancel: () => void;
  /** Asks for the menu of an entry, or of the project folder (null), at a point of the window. */
  onMenu: (row: ExplorerEntry | null, point: Readonly<{ x: number; y: number }>) => void;
  onAction: (action: "rename" | "delete", row: ExplorerEntry) => void;
  /** Moves an entry into a folder ("" is the project folder). */
  onMove: (path: string, folder: string) => void;
}) {
  const list = useRef<HTMLDivElement>(null);
  const typed = useRef({ text: "", at: 0 });
  const dragged = useRef<string | null>(null);
  const entries = treeEntryRows(rows);
  const windowed = useWindowedRows(list, rows.length, explorerRowHeight);
  const latest = useRef({ onSelect, onOpen, onToggle, onMenu, onMove }); latest.current = { onSelect, onOpen, onToggle, onMenu, onMove };
  useEffect(() => { handle.current = { focus: () => list.current?.focus({ preventScroll: true }) }; return () => { handle.current = null; }; }, [handle]);
  // The selected row, and the name being typed, are kept in view.
  const focusIndex = rows.findIndex(row => row.kind === "input" || row.kind === "entry" && row.path === selected);
  useLayoutEffect(() => { if (focusIndex >= 0) windowed.reveal(focusIndex); }, [focusIndex, selected]);

  const click = useRef((row: ExplorerEntry) => {
    latest.current.onSelect(row.path);
    if (row.directory) latest.current.onToggle(row.path); else latest.current.onOpen(row.path, false);
  });
  const open = useRef((row: ExplorerEntry) => { if (!row.directory) latest.current.onOpen(row.path, true); });
  const menu = useRef((row: ExplorerEntry, point: Readonly<{ x: number; y: number }>) => { latest.current.onSelect(row.path); latest.current.onMenu(row, point); });
  const dragStart = useRef((row: ExplorerEntry) => { dragged.current = row.path; });
  const dropInto = useRef((folder: string) => {
    const path = dragged.current;
    dragged.current = null;
    // Not onto itself, into what it holds, or into the folder it is already in.
    if (path !== null && path !== folder && !folder.startsWith(`${path}/`) && parentTreePath(path) !== folder) latest.current.onMove(path, folder);
  });

  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing || event.target !== event.currentTarget) return;
    const at = entries.findIndex(row => row.path === selected), row = at >= 0 ? entries[at] : undefined;
    const move = (index: number) => { if (entries.length) onSelect(entries[Math.max(0, Math.min(entries.length - 1, index))].path); };
    const plain = !event.ctrlKey && !event.metaKey && !event.altKey;
    if ((event.key === "ContextMenu" || event.key === "F10" && event.shiftKey) && row) {
      const box = list.current?.querySelector(`[data-path="${CSS.escape(row.path)}"]`)?.getBoundingClientRect();
      onMenu(row, { x: (box?.left ?? 0) + 40, y: (box?.bottom ?? 0) });
    } else if (!plain) return;
    else if (event.key === "ArrowDown") move(at + 1);
    else if (event.key === "ArrowUp") move(at < 0 ? entries.length - 1 : at - 1);
    else if (event.key === "Home") move(0);
    else if (event.key === "End") move(entries.length - 1);
    else if (event.key === "PageDown") move(Math.max(at, 0) + 12);
    else if (event.key === "PageUp") move(Math.max(at, 0) - 12);
    else if (event.key === "ArrowRight" && row?.directory) { if (!row.expanded) onToggle(row.path); else move(at + 1); }
    else if (event.key === "ArrowLeft" && row) { if (row.directory && row.expanded) onToggle(row.path); else { const parent = parentTreePath(row.path); if (parent) onSelect(parent); } }
    else if (event.key === "Enter" && row) { if (row.directory) onToggle(row.path); else onOpen(row.path, true); }
    else if (event.key === " " && row) { if (row.directory) onToggle(row.path); else onOpen(row.path, false); }
    else if (event.key === "F2" && row) onAction("rename", row);
    else if (event.key === "Delete" && row) onAction("delete", row);
    else if (event.key.length === 1 && event.key !== " ") {
      // Letters typed in a row go to the next name that starts with them.
      const now = Date.now();
      typed.current = { text: now - typed.current.at < 700 ? typed.current.text + event.key : event.key, at: now };
      const found = treeTypeAhead(entries, selected, typed.current.text);
      if (found !== null) onSelect(found);
    } else return;
    event.preventDefault();
  }

  return <div ref={list} className="editor-tree" role="tree" tabIndex={0} aria-label={label} data-rename-keys onScroll={windowed.onScroll} onKeyDown={keyDown}
    onContextMenu={event => { if (event.target === event.currentTarget || (event.target as HTMLElement).classList.contains("editor-tree-space")) { event.preventDefault(); onMenu(null, { x: event.clientX, y: event.clientY }); } }}
    onDragOver={event => { if (event.dataTransfer.types.includes("application/x-codealta-tree-entry")) event.preventDefault(); }}
    onDrop={event => { event.preventDefault(); dropInto.current(""); }}>
    <div style={{ height: windowed.before }} />
    {rows.slice(windowed.first, windowed.last).map(row => row.kind === "entry"
      ? <EntryRow key={row.key} row={row} selected={row.path === selected} active={row.path === active} status={row.directory ? undefined : decorations.files.get(row.path)}
          changed={row.directory && decorations.folders.has(row.path)} onClick={click.current} onOpen={open.current} onMenu={menu.current}
          onDragStart={dragStart.current} onDropInto={dropInto.current} />
      : row.kind === "input" ? <NameInput key={row.key} row={row} tree={tree} onCommit={name => onCommit({ parent: row.parent, directory: row.directory, path: row.path }, name)} onCancel={onCancel} />
      : <TreeNote key={row.key} row={row} />)}
    <div className="editor-tree-space" style={{ height: windowed.after + 28 }} />
  </div>;
}

function TreeNote({ row }: { row: Extract<TreeRow, { kind: "note" }> }) {
  const { t } = useShellLanguage();
  return <div className="editor-tree-row editor-tree-note" style={indent(row.depth)}>
    {t(row.note === "truncated" ? "More entries are not shown." : "This folder could not be read.")}</div>;
}
