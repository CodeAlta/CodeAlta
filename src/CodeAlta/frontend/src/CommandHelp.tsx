import { useRef, useState } from "react";
import { InputGroup } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { AppWindow } from "./AppWindow";
import { KeyGesture } from "./KeyGesture";
import { commandCategories, commandDefinitions, commandKeys } from "./commandRegistry";
import type { PluginCommandView } from "./pluginUi";
import { useShellLanguage } from "./shellLanguage";

/** Extra prompt-editor keys that are not commands. */
const editorKeys: readonly (readonly [string, "New line" | "Previous sent prompt" | "Next sent prompt" | "Reference a project file" | "Reference an issue"])[] = [
  ["Shift+Enter", "New line"], ["Alt+Up", "Previous sent prompt"], ["Alt+Down", "Next sent prompt"], ["@", "Reference a project file"], ["#", "Reference an issue"],
];

/** The help window (F1, or "?" in an empty prompt): every command with its shortcuts, grouped like the terminal UI's help. */
export function CommandHelp({ onClose, pluginCommands = [] }: { onClose: () => void; pluginCommands?: readonly PluginCommandView[] }) {
  const { t } = useShellLanguage();
  const [filter, setFilter] = useState("");
  const filterInput = useRef<HTMLInputElement>(null);
  const words = filter.trim().toLowerCase().split(/\s+/u).filter(Boolean);
  const shown = (text: string) => words.every(word => text.toLowerCase().includes(word));
  const groups = commandCategories.map(category => ({ category,
    rows: commandDefinitions.filter(command => command.category === category && commandKeys(command).length > 0
      && shown(`${command.name} ${t(command.label)} ${t(command.description)} ${commandKeys(command).join(" ")}`))
      .sort((left, right) => t(left.label).localeCompare(t(right.label))) })).filter(group => group.rows.length > 0);
  const editor = editorKeys.filter(([keys, label]) => shown(`${keys} ${t(label)}`));
  // Plugin commands are listed with their slash name: they run from the palette even without a shortcut.
  const plugins = pluginCommands.filter(command => command.help && shown(`${command.name} ${command.label} ${command.description} ${command.plugin} ${command.keys ?? ""}`))
    .sort((left, right) => left.label.localeCompare(right.label));
  return <AppWindow storageKey="codealta.desktop.window.help.v1" className="command-help-dialog" titleId="shortcut-title" title={t("Commands and shortcuts")}
    preferredSize={viewport => ({ width: Math.min(980, viewport.width - 40), height: Math.min(720, viewport.height - 40) })} minimumSize={{ width: 420, height: 320 }}
    onClose={onClose} closeLabel={t("Close")} onOpened={() => filterInput.current?.focus()} onCancel={event => { event.preventDefault(); onClose(); }}
    // Escape closes the window even from the filter field, where it would otherwise only clear the text.
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); } }}>
    <div className="command-help">
      <InputGroup inputRef={filterInput} type="search" className="command-help-filter" value={filter} leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />}
        placeholder={t("Filter commands and shortcuts")} aria-label={t("Filter commands and shortcuts")} onChange={event => setFilter(event.target.value)} />
      <div className="command-help-groups">
        {groups.map(group => <section key={group.category} aria-label={t(group.category)}>
          <h3>{t(group.category)}</h3>
          <dl>{group.rows.map(command => <div key={command.id}>
            <dt><strong>{t(command.label)}</strong><span>{t(command.description)}</span></dt>
            <dd>{commandKeys(command).map(gesture => <KeyGesture key={gesture} gesture={gesture} />)}</dd>
          </div>)}</dl>
        </section>)}
        {editor.length > 0 && <section aria-label={t("Prompt editor")}>
          <h3>{t("Prompt editor")}</h3>
          <dl>{editor.map(([keys, label]) => <div key={keys}><dt><strong>{t(label)}</strong></dt><dd><KeyGesture gesture={keys} /></dd></div>)}</dl>
        </section>}
        {plugins.length > 0 && <section aria-label={t("Plugins")}>
          <h3>{t("Plugins")}</h3>
          <dl>{plugins.map(command => <div key={command.id}>
            <dt><strong>{command.label}</strong><span>{command.description}</span></dt>
            <dd>{command.keys ? <KeyGesture gesture={command.keys} /> : <code>/{command.name}</code>}</dd>
          </div>)}</dl>
        </section>}
        {groups.length === 0 && editor.length === 0 && plugins.length === 0 && <p className="bp6-text-muted">{t("No command matches.")}</p>}
      </div>
    </div>
  </AppWindow>;
}
