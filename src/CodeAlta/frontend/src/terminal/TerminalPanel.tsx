import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { Button, InputGroup, PopoverNext } from "@blueprintjs/core";
import type { TerminalItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { SessionTabMenu, type SessionMenuEntry } from "../SessionTabMenu";
import { subscribeAppearance } from "../shellColors";
import { useShellLanguage } from "../shellLanguage";
import { TerminalRename } from "./TerminalList";
import type { TerminalLook } from "./terminalLook";
import { TerminalOptions } from "./TerminalOptions";
import type { TerminalMatches, TerminalSearch } from "./TerminalSurface";
import type { TerminalWorkspace } from "./terminalWorkspace";

const modalOpen = () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');

function Toggle({ icon, label, active, onChange }: { icon: IconName; label: string; active: boolean; onChange: (value: boolean) => void }) {
  return <Button variant="minimal" size="small" className="terminal-find-toggle" icon={<AppIcon name={icon} size={15} />} active={active} aria-pressed={active}
    aria-label={label} title={label} onClick={() => onChange(!active)} />;
}

/**
 * The tab of a terminal: a line that says what runs and where, and the terminal under it. The terminal belongs
 * to the application: closing the tab leaves it running, and it is listed under its project.
 */
export function TerminalPanel({ workspace, id, terminal, visible, active, look, onLook, onActivate, onCreate, rename, onCloseTerminal }: {
  workspace: TerminalWorkspace; id: string;
  /** What the host says of the terminal; nothing once it is gone. */
  terminal: TerminalItem | undefined;
  /** Whether the tab is the one shown in its pane. */
  visible: boolean;
  /** Whether the tab is the active one of the window. */
  active: boolean;
  look: TerminalLook; onLook: (look: TerminalLook) => void;
  onActivate: () => void;
  /** Opens another terminal in the same folder. */
  onCreate: () => void;
  /** Renames a terminal; resolves with the status the host answered. */
  rename: (id: string, title: string) => Promise<string>;
  /** Ends the program of the terminal. */
  onCloseTerminal: () => void;
}) {
  const { t } = useShellLanguage();
  const root = useRef<HTMLElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const findInput = useRef<HTMLInputElement>(null);
  const menuButton = useRef<HTMLButtonElement>(null);
  const [finding, setFinding] = useState(false);
  const [search, setSearch] = useState<TerminalSearch>({ text: "", caseSensitive: false, wholeWord: false, regex: false });
  const [matches, setMatches] = useState<TerminalMatches>({ index: -1, count: 0 });
  const [menu, setMenu] = useState<{ at?: { x: number; y: number }; selection: boolean } | null>(null);
  const [renaming, setRenaming] = useState(false);
  const progress = useSyncExternalStore(workspace.subscribe, () => workspace.progress(id));
  const system = useSyncExternalStore(workspace.hub.subscribe, workspace.hub.system);
  const latest = useRef({ search }); latest.current = { search };

  // The terminal of this tab: created the first time, shown here once its font is ready, and kept while the tab lasts.
  useLayoutEffect(() => {
    const surface = workspace.acquire(id);
    let mounted = true;
    void workspace.fonts().then(() => { if (mounted && host.current) surface.mount(host.current); });
    workspace.listen(id, {
      action: action => { if (action === "find") openFind(); },
      matches: setMatches,
    });
    const resized = new ResizeObserver(() => surface.fit());
    if (host.current) resized.observe(host.current);
    return () => {
      mounted = false;
      resized.disconnect();
      workspace.listen(id, null);
      surface.unmount();
      workspace.release(id);
    };
  }, [workspace, id]);

  useEffect(() => {
    workspace.hub.show(id, visible);
    if (visible) workspace.surface(id)?.fit();
    return () => workspace.hub.show(id, false);
  }, [workspace, id, visible]);

  // The active tab takes the keyboard, unless something of the panel already has it.
  useEffect(() => {
    if (!active || modalOpen() || root.current?.contains(document.activeElement) && document.activeElement !== root.current) return;
    void workspace.fonts().then(() => { if (!modalOpen()) workspace.surface(id)?.focus(); });
  }, [active, workspace, id]);

  // The places a text is found at, in colors that are read on a dark and on a light terminal.
  const colors = () => document.documentElement.dataset.theme === "light" ? { match: "#f5df9a", active: "#f0b726" } : { match: "#5c4a14", active: "#8f6a12" };
  function find(next: TerminalSearch, forward: boolean, incremental: boolean) {
    workspace.surface(id)?.find(next, forward, colors(), incremental);
    if (!next.text) setMatches({ index: -1, count: 0 });
  }
  // What was found is drawn again in the colors of the theme when the theme changes.
  useEffect(() => finding ? subscribeAppearance(() => { if (latest.current.search.text) find(latest.current.search, true, true); }) : undefined, [finding, workspace, id]);
  function openFind() {
    setFinding(true);
    const selected = workspace.surface(id)?.selection ?? "";
    if (selected && !selected.includes("\n") && selected.length <= 200) setSearch(current => ({ ...current, text: selected }));
    requestAnimationFrame(() => { findInput.current?.focus(); findInput.current?.select(); });
  }
  function closeFind() {
    setFinding(false);
    workspace.surface(id)?.clearFind();
    workspace.surface(id)?.focus();
  }
  function change(next: TerminalSearch) {
    setSearch(next);
    find(next, true, true);
  }
  async function paste() {
    try { workspace.surface(id)?.paste(await navigator.clipboard.readText()); }
    catch { /* The clipboard cannot be read: Ctrl+V pastes. */ }
    workspace.surface(id)?.focus();
  }

  const running = terminal?.running ?? false;
  const state = !terminal ? "" : !terminal.running ? t("Ended (exit code {code})", { code: terminal.exitCode ?? 0 })
    : terminal.busy ? terminal.command ?? t("Running") : "";
  // What is done to the text of the terminal: the menu under the pointer.
  const editing = (selection: boolean): SessionMenuEntry[] => [
    { key: "copy", label: t("Copy"), icon: "copy", disabled: !selection, onSelect: () => workspace.surface(id)?.copySelection() },
    { key: "paste", label: t("Paste"), icon: "paste", disabled: !running, onSelect: () => void paste() },
    { key: "all", label: t("Select all"), icon: "selectAll", onSelect: () => workspace.surface(id)?.selectAll() },
    { key: "find", label: `${t("Find")}…`, icon: "search", onSelect: openFind },
    { key: "clear", label: t("Clear"), icon: "eraser", onSelect: () => { workspace.surface(id)?.clear(); workspace.surface(id)?.focus(); } },
  ];
  // What is done to the terminal itself: the menu of the panel.
  const actions: SessionMenuEntry[] = [
    { key: "rename", label: t("Rename…"), icon: "edit", disabled: !terminal, onSelect: () => setRenaming(true) },
    { key: "clear", label: t("Clear"), icon: "eraser", onSelect: () => { workspace.surface(id)?.clear(); workspace.surface(id)?.focus(); } },
    { key: "close", label: t(running ? "End terminal" : "Close terminal"), icon: "trash", danger: true, onSelect: onCloseTerminal },
  ];

  return <section ref={root} className="terminal-panel" tabIndex={-1} data-active={active} data-terminal-keys aria-label={terminal?.title ?? t("Terminal")}
    onFocusCapture={onActivate} onPointerDownCapture={onActivate}
    onKeyDownCapture={event => {
      if (event.key === "Escape" && finding && findInput.current?.contains(event.target as Node)) { event.preventDefault(); event.stopPropagation(); closeFind(); }
    }}>
    <header className="terminal-header">
      <span className="terminal-shell">{terminal?.profileName ?? ""}</span>
      <span className="terminal-folder" title={terminal?.folder}><bdi>{terminal?.folder ?? ""}</bdi></span>
      {state && <span className="terminal-state" data-ended={!running} title={state}>
        {running && <ActivitySpinner size={12} />}{state}</span>}
      {progress && progress.state !== 0 && <span className="terminal-progress" data-state={progress.state} role="progressbar" aria-valuemin={0} aria-valuemax={100}
        aria-valuenow={progress.state === 3 ? undefined : progress.value}><span style={{ width: `${progress.state === 3 ? 100 : Math.max(2, Math.min(100, progress.value))}%` }} /></span>}
      <span className="terminal-actions">
        <Button variant="minimal" size="small" icon={<AppIcon name="plus" size={15} />} aria-label={t("New terminal")} title={t("New terminal")} onClick={onCreate} />
        <Button variant="minimal" size="small" icon={<AppIcon name="search" size={15} />} active={finding} aria-label={t("Find")} title={`${t("Find")} (Ctrl+F)`}
          onClick={() => finding ? closeFind() : openFind()} />
        <PopoverNext content={<TerminalOptions look={look} onLook={onLook} shells={system?.profiles} />} placement="bottom-end" popoverClassName="terminal-options-popover">
          <Button variant="minimal" size="small" icon={<AppIcon name="filter" size={15} />} aria-label={t("Terminal options")} title={t("Terminal options")} />
        </PopoverNext>
        <Button ref={menuButton} variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("More actions")} title={t("More actions")}
          aria-haspopup="menu" aria-expanded={!!menu && !menu.at} onClick={() => setMenu(current => current ? null : { selection: !!workspace.surface(id)?.selection })} />
      </span>
      {renaming && terminal && <TerminalRename terminal={terminal} rename={rename} onDone={() => { setRenaming(false); workspace.surface(id)?.focus(); }} />}
    </header>
    <div className="terminal-host" ref={host} onContextMenu={event => {
      event.preventDefault();
      setMenu({ at: { x: event.clientX, y: event.clientY }, selection: !!workspace.surface(id)?.selection });
    }} />
    {finding && <div className="terminal-find" role="search">
      <InputGroup inputRef={findInput} size="small" className="terminal-find-query" value={search.text} placeholder={t("Find")} aria-label={t("Find")}
        onChange={event => change({ ...search, text: event.target.value })}
        onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); find(latest.current.search, !event.shiftKey, false); } }}
        rightElement={<span className="terminal-find-toggles">
          <Toggle icon="caseSensitive" label={t("Match case")} active={search.caseSensitive} onChange={value => change({ ...search, caseSensitive: value })} />
          <Toggle icon="wholeWord" label={t("Match whole word")} active={search.wholeWord} onChange={value => change({ ...search, wholeWord: value })} />
          <Toggle icon="regex" label={t("Use regular expression")} active={search.regex} onChange={value => change({ ...search, regex: value })} />
        </span>} />
      <span className="terminal-find-count" data-empty={!!search.text && matches.count === 0}>{search.text ? `${matches.count === 0 ? 0 : matches.index + 1}/${matches.count}` : ""}</span>
      <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={15} />} aria-label={t("Previous match")} title={t("Previous match")} disabled={!search.text} onClick={() => find(search, false, false)} />
      <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={15} />} aria-label={t("Next match")} title={t("Next match")} disabled={!search.text} onClick={() => find(search, true, false)} />
      <Button variant="minimal" size="small" icon={<AppIcon name="close" size={15} />} aria-label={t("Close")} title={t("Close")} onClick={closeFind} />
    </div>}
    {menu && menuButton.current && <SessionTabMenu anchor={menuButton.current} at={menu.at} container={document.body} title={t("More actions")}
      current={() => true} onClose={() => queueMicrotask(() => setMenu(null))} items={menu.at ? editing(menu.selection) : actions} />}
  </section>;
}
