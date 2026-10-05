import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { commandKeys, searchCommands, type CommandDefinition, type CommandId } from "./commandRegistry";
import { searchPluginCommands, type PluginCommandView } from "./pluginUi";
import { useShellLanguage } from "./shellLanguage";

const pageStep = 8;

/** Renders a gesture such as "Ctrl+G Ctrl+T" as key caps, chord strokes separated by a thin gap. */
export function KeyGesture({ gesture }: { gesture: string }) {
  return <span className="key-gesture">{gesture.split(" ").map((stroke, index) =>
    <span key={index} className="key-stroke">{stroke.split("+").map((part, at) => <kbd key={at}>{part}</kbd>)}</span>)}</span>;
}

/**
 * The command palette (Ctrl+P, or "/" in an empty prompt): search by slash name, label or description
 * and run a command with Enter. Commands that cannot run right now are listed but dimmed. It is a
 * movable, resizable window like the others; its place and size are remembered.
 */
export function CommandPalette({ available, onChoose, onClose, pluginCommands = [], onChoosePlugin }: {
  available: (id: CommandId) => boolean; onChoose: (id: CommandId) => void; onClose: () => void;
  /** The commands of plugins, listed after the application's own in a group of theirs. */
  pluginCommands?: readonly PluginCommandView[]; onChoosePlugin?: (id: string) => void;
}) {
  const { t } = useShellLanguage();
  const search = useRef<HTMLInputElement>(null);
  const results = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const own = useMemo(() => searchCommands(query, command => t(command.label), command => t(command.description)), [query, t]);
  const plugins = useMemo(() => searchPluginCommands(query, pluginCommands), [query, pluginCommands]);
  type Row = { key: string; name: string; label: string; description: string; group: string; keys: readonly string[]; enabled: boolean; run: () => void };
  const matches = useMemo<Row[]>(() => [
    ...own.map(command => ({ key: command.id, name: command.name, label: t(command.label), description: t(command.description), group: t(command.category),
      keys: commandKeys(command), enabled: available(command.id), run: () => onChoose(command.id) })),
    ...plugins.map(command => ({ key: `plugin-${command.id}`, name: command.name, label: command.label, description: command.description,
      group: command.group ?? command.plugin, keys: command.keys ? [command.keys] : [], enabled: true, run: () => onChoosePlugin?.(command.id) })),
  ], [own, plugins, t, available, onChoose, onChoosePlugin]);
  const index = Math.max(0, Math.min(active, matches.length - 1));
  const grouped = query.trim() === "";
  useLayoutEffect(() => { results.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [index, query]);
  const run = (row: Row | undefined) => { if (row?.enabled) row.run(); };
  const move = (delta: number) => { if (matches.length) setActive((index + delta + matches.length * pageStep) % matches.length); };
  return <AppWindow storageKey="codealta.desktop.window.palette.v1" className="command-palette" titleId="palette-title"
    title={<><AppIcon name="search" size={14} /> {t("Command Palette")}</>}
    preferredSize={viewport => ({ width: Math.min(820, viewport.width - 32), height: Math.min(560, viewport.height - 64) })}
    minimumSize={{ width: 380, height: 220 }} onClose={onClose} closeLabel={t("Close")} onOpened={() => search.current?.focus()}
    onClick={event => { if (event.target === event.currentTarget) onClose(); }}
    onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return;
      const handled = event.key === "Escape" ? (onClose(), true)
        : event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (setActive(Math.min(matches.length - 1, index + pageStep)), true)
        : event.key === "PageUp" ? (setActive(Math.max(0, index - pageStep)), true)
        : event.key === "Enter" ? (run(matches[index]), true) : false;
      if (handled) event.preventDefault();
    }}>
      <div className="command-palette-search"><AppIcon name="search" size={16} />
        <input ref={search} id="palette-search" type="text" role="combobox" aria-autocomplete="list" aria-expanded="true" spellCheck={false}
          aria-controls="palette-results" aria-activedescendant={matches[index] ? `palette-option-${index}` : undefined}
          placeholder={t("Type a command…")} aria-label={t("Search commands")} value={query}
          onChange={event => { setQuery(event.target.value); setActive(0); }} /></div>
      <div ref={results} id="palette-results" className="command-palette-results" role="listbox" aria-label={t("Available commands")}>
        {matches.map((row, at) => <div key={row.key}>
          {grouped && (at === 0 || matches[at - 1].group !== row.group) && <div className="command-palette-group" role="presentation">{row.group}</div>}
          <div role="option" id={`palette-option-${at}`} aria-selected={at === index} aria-disabled={!row.enabled}
            className="command-palette-row" onMouseMove={() => { if (at !== index) setActive(at); }} onClick={() => run(row)}>
            <span className="command-palette-name">/{row.name}</span>
            <span className="command-palette-label">{row.label}</span>
            <span className="command-palette-description">{row.description}</span>
            <span className="command-palette-keys">{row.keys.slice(0, 2).map(gesture => <KeyGesture key={gesture} gesture={gesture} />)}</span>
          </div></div>)}
        {!matches.length && <p className="command-palette-empty" role="status">{t("No command matches.")}</p>}
      </div>
  </AppWindow>;
}
