import { useState } from "react";
import type { TerminalItem } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { RenamePopover } from "../RenamePopover";
import { isSessionContextKey } from "../sessionRowActions";
import { SessionTabMenu } from "../SessionTabMenu";
import { useShellLanguage } from "../shellLanguage";

/** The form that gives a terminal a title, beside the row or the header it is rendered in. An empty title gives back the one the terminal has by itself. */
export function TerminalRename({ terminal, rename, onDone }: {
  terminal: TerminalItem;
  /** Renames the terminal; resolves with the status the host answered. */
  rename: (id: string, title: string) => Promise<string>;
  onDone: () => void;
}) {
  const { t } = useShellLanguage();
  const [value, setValue] = useState(terminal.titled ? terminal.title : "");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  return <RenamePopover label={t("Terminal title")} value={value} busy={busy} error={failed ? t("The terminal could not be renamed.") : null}
    onChange={next => { setValue(next); setFailed(false); }} onCancel={onDone}
    onSubmit={() => {
      setBusy(true);
      void rename(terminal.id, value.trim()).then(status => status === "ok", () => false).then(done => {
        setBusy(false);
        if (done) onDone(); else setFailed(true);
      });
    }} />;
}

/**
 * The terminals of a project in the Explorer, under its sessions. A terminal runs with or without a tab: its
 * row opens its tab, and its two buttons rename it and end it.
 */
export function TerminalList({ terminals, active, onOpen, onClose, rename }: {
  terminals: readonly TerminalItem[];
  /** The terminal whose tab is the active one of the window, if any. */
  active: string | null;
  onOpen: (terminal: TerminalItem) => void;
  /** Ends the program of a terminal. */
  onClose: (terminal: TerminalItem) => void;
  rename: (id: string, title: string) => Promise<string>;
}) {
  const { t } = useShellLanguage();
  const [renaming, setRenaming] = useState<string | null>(null);
  const [menu, setMenu] = useState<{ id: string; anchor: HTMLElement; at?: { x: number; y: number } } | null>(null);
  if (terminals.length === 0) return null;
  return <div className="terminal-list session-list" role="group" aria-label={t("Terminals")}>
    <div className="terminal-list-title" role="presentation"><AppIcon name="terminal" size={11} /><span>{t("Terminals")}</span></div>
    {terminals.map(terminal => {
      const state = !terminal.running ? t("Ended (exit code {code})", { code: terminal.exitCode ?? 0 }) : terminal.busy ? terminal.command ?? t("Running") : terminal.profileName;
      const shown = menu?.id === terminal.id ? menu : null;
      return <div className={`session-row terminal-row${shown ? " menu-open" : ""}`} key={terminal.id} data-ended={!terminal.running}
        onContextMenu={event => {
          if ((event.target as HTMLElement).closest("input, textarea, select, [contenteditable='true']")) return;
          event.preventDefault();
          setMenu({ id: terminal.id, anchor: event.currentTarget, at: { x: event.clientX, y: event.clientY } });
        }}
        onKeyDown={event => {
          if ((event.target as HTMLElement).closest("input")) return;
          if (event.key === "F2") { event.preventDefault(); event.stopPropagation(); setRenaming(terminal.id); return; }
          if (!isSessionContextKey(event.key, event.shiftKey, event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229, false)) return;
          event.preventDefault(); event.stopPropagation();
          setMenu({ id: terminal.id, anchor: event.currentTarget });
        }}>
        <button type="button" aria-pressed={active === terminal.id} title={`${terminal.title}\n${terminal.folder}\n${state}`} style={{ paddingLeft: 11 }}
          data-rename-keys onClick={() => onOpen(terminal)} onDoubleClick={() => setRenaming(terminal.id)}>
          <span className="session-icon" data-file-tone={terminal.running ? "green" : "muted"}><AppIcon name="terminal" size={13} /></span>
          <span className="terminal-row-title session-title" data-folder={!terminal.titled}><bdi>{terminal.title}</bdi></span>
          <span className="terminal-row-meta">
            {terminal.attention && <span className="terminal-attention" role="img" aria-label={t("Asked for attention")} title={t("Asked for attention")} />}
            {terminal.agent && <span className="terminal-agent" role="img" aria-label={t("Created by a session")} title={t("Created by a session")}><AppIcon name="assistant" size={12} /></span>}
            {terminal.running && terminal.busy ? <ActivitySpinner size={12} /> : !terminal.running ? <span>{terminal.exitCode ?? 0}</span> : <span>{terminal.profileName}</span>}
          </span>
        </button>
        <span className="terminal-row-actions">
          <button type="button" className="icon-button terminal-row-action" aria-label={t("Rename {title}", { title: terminal.title })} title={t("Rename…")}
            onClick={() => setRenaming(terminal.id)}><AppIcon name="edit" size={13} /></button>
          <button type="button" className="icon-button terminal-row-action" aria-label={t(terminal.running ? "End {title}" : "Close {title}", { title: terminal.title })}
            title={t(terminal.running ? "End terminal" : "Close terminal")} onClick={() => onClose(terminal)}><AppIcon name="close" size={14} /></button>
        </span>
        {renaming === terminal.id && <TerminalRename terminal={terminal} rename={rename} onDone={() => setRenaming(null)} />}
        {shown && <SessionTabMenu anchor={shown.anchor} at={shown.at} container={document.body} title={t("Terminal options")}
          current={() => true} onClose={() => queueMicrotask(() => setMenu(value => value === shown ? null : value))}
          items={[
            { key: "open", label: t("Open terminal"), icon: "terminal", onSelect: () => onOpen(terminal) },
            { key: "rename", label: t("Rename…"), icon: "edit", onSelect: () => setRenaming(terminal.id) },
            { key: "close", label: t(terminal.running ? "End terminal" : "Close terminal"), icon: "trash", danger: true, onSelect: () => onClose(terminal) },
          ]} />}
      </div>;
    })}
  </div>;
}
