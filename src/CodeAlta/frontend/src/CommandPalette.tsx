import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { commandKeys, searchCommands, type CommandDefinition, type CommandId } from "./commandRegistry";
import { useShellLanguage } from "./shellLanguage";

const pageStep = 8;

/** Renders a gesture such as "Ctrl+G Ctrl+T" as key caps, chord strokes separated by a thin gap. */
export function KeyGesture({ gesture }: { gesture: string }) {
  return <span className="key-gesture">{gesture.split(" ").map((stroke, index) =>
    <span key={index} className="key-stroke">{stroke.split("+").map((part, at) => <kbd key={at}>{part}</kbd>)}</span>)}</span>;
}

/**
 * The command palette (Ctrl+P, or "/" in an empty prompt): search by slash name, label or description
 * and run a command with Enter. Commands that cannot run right now are listed but dimmed.
 */
export function CommandPalette({ available, onChoose, onClose }: {
  available: (id: CommandId) => boolean; onChoose: (id: CommandId) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const results = useRef<HTMLDivElement>(null);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const matches = useMemo(() => searchCommands(query, command => t(command.label), command => t(command.description)), [query, t]);
  const index = Math.max(0, Math.min(active, matches.length - 1));
  const grouped = query.trim() === "";
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, []);
  useLayoutEffect(() => { results.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [index, query]);
  const run = (command: CommandDefinition | undefined) => { if (command && available(command.id)) onChoose(command.id); };
  const move = (delta: number) => { if (matches.length) setActive((index + delta + matches.length * pageStep) % matches.length); };
  return <dialog ref={dialog} className="command-palette" aria-modal="true" aria-label={t("Command Palette")}
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
    <div className="command-palette-surface">
      <div className="command-palette-search"><AppIcon name="search" size={16} />
        <input autoFocus id="palette-search" type="text" role="combobox" aria-autocomplete="list" aria-expanded="true" spellCheck={false}
          aria-controls="palette-results" aria-activedescendant={matches[index] ? `palette-option-${matches[index].id}` : undefined}
          placeholder={t("Type a command…")} aria-label={t("Search commands")} value={query}
          onChange={event => { setQuery(event.target.value); setActive(0); }} /></div>
      <div ref={results} id="palette-results" className="command-palette-results" role="listbox" aria-label={t("Available commands")}>
        {matches.map((command, at) => <div key={command.id}>
          {grouped && (at === 0 || matches[at - 1].category !== command.category) && <div className="command-palette-group" role="presentation">{t(command.category)}</div>}
          <div role="option" id={`palette-option-${command.id}`} aria-selected={at === index} aria-disabled={!available(command.id)}
            className="command-palette-row" onMouseMove={() => { if (at !== index) setActive(at); }} onClick={() => run(command)}>
            <span className="command-palette-name">/{command.name}</span>
            <span className="command-palette-label">{t(command.label)}</span>
            <span className="command-palette-description">{t(command.description)}</span>
            <span className="command-palette-keys">{commandKeys(command).slice(0, 2).map(gesture => <KeyGesture key={gesture} gesture={gesture} />)}</span>
          </div></div>)}
        {!matches.length && <p className="command-palette-empty" role="status">{t("No command matches.")}</p>}
      </div>
    </div>
  </dialog>;
}
