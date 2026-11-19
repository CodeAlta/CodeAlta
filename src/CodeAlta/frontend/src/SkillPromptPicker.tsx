import { useContext, useId, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import { InputGroup } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { usePromptPicker } from "./promptPicker";
import { searchSkills } from "./search/skillResults";
import { activeSkillReference, insertSkillCall, SkillChoicesContext, typedSkillQuery } from "./skillMentions";
import { skillSourceLabels } from "./skillSources";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

const pageStep = 8;

/**
 * The `/` picker of a prompt editor: typing `/` at a word start of a prompt that has text opens a search window listing
 * the skills of the project, found by their name, then their title, then their description. Enter writes `/name ` in
 * place of the `/query`, where the user goes on typing; the skill is given to the model when the prompt is sent.
 * Escape, Enter without a match, or a space or a `/` typed in its search, close it with what was typed written back in
 * the prompt (a path such as `/usr/local/bin` stays text); Ctrl+Space opens it again on a `/token`. A `/` in an empty
 * prompt opens the search of the window instead.
 */
export function SkillPromptPicker({ edit, input }: { edit: (text: string) => void; input: RefObject<PromptInput | null> }) {
  const { t } = useShellLanguage();
  const choices = useContext(SkillChoicesContext);
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const listId = useId();
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState(0);
  // Only typing `/` opens it by itself: the `/name` it writes is a token, which a deletion or a click leaves as text.
  const { trigger, dialog, close } = usePromptPicker({ input, edit, enabled: !!choices, focus: search, detect: activeSkillReference, typedOnly: "/",
    onOpen: value => { setQuery(value.query); setSelected(0); choices?.refresh(); } });
  const skills = choices?.skills ?? null;
  const found = useMemo(() => skills ? searchSkills(skills, query) : [], [skills, query]);
  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [selected, found]);

  const count = found.length;
  const index = Math.max(0, Math.min(selected, count - 1));
  const move = (delta: number) => setSelected(Math.max(0, Math.min(count - 1, index + delta)));
  function choose(at: number) {
    const row = found[at];
    const element = input.current;
    if (!row || !trigger || !element || element.value !== trigger.text) return;
    const next = insertSkillCall(trigger.text, trigger.start, trigger.end, row.skill.name);
    if (next) close(next);
  }
  // No skill is chosen: what was typed after the `/` goes back in the prompt, and the token stays closed.
  function keepTyped(typed: string) {
    const element = input.current;
    const next = trigger && element?.value === trigger.text && typed ? typedSkillQuery(trigger.text, trigger.start, trigger.end, typed) : null;
    close(next ? { ...next, keepClosed: true } : undefined);
  }
  return trigger && <dialog ref={dialog} className="app-dialog reference-palette" aria-modal="true" aria-labelledby={`${listId}-title`}
    onCancel={event => { event.preventDefault(); keepTyped(query); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.metaKey || event.ctrlKey || event.altKey) return;
      const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
        : event.key === "Enter" || event.key === "Tab" && !event.shiftKey ? (count ? choose(index) : keepTyped(query), true)
        : event.key === "Escape" ? (keepTyped(query), true) : false;
      if (handled) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.skill-picker.v1" titleId={`${listId}-title`}
      title={<><AppIcon name="skill" size={14} /> {t("Skills")}</>}
      preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
      minimumSize={{ width: 420, height: 240 }} onClose={() => keepTyped(query)} closeLabel={t("Close")}
      headerActions={!skills && <span className="reference-status" role="status"><ActivitySpinner size={12} />{t("Loading…")}</span>}>
      <InputGroup inputRef={search} className="reference-search" type="search" maxLength={128} value={query} spellCheck={false}
        leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={t("Search skills…")}
        role="combobox" aria-expanded="true" aria-controls={listId} aria-label={t("Search skills…")}
        aria-activedescendant={found[index] ? `${listId}-${index}` : undefined}
        onChange={event => {
          const value = event.target.value;
          if (/[\s/]/u.test(value)) keepTyped(value);
          else { setQuery(value); setSelected(0); }
        }} />
      <div id={listId} ref={list} role="listbox" aria-label={t("Skills")} className="reference-list">
        {found.map(({ skill }, at) => <div role="option" id={`${listId}-${at}`} key={skill.name} aria-selected={at === index} className="plugin-picker-row skill-picker-row"
          title={`/${skill.name}\n${skill.description}`} onMouseMove={() => { if (at !== index) setSelected(at); }} onClick={() => choose(at)}>
          <span className="plugin-picker-label"><code className="global-search-skill-name">/{skill.name}</code></span>
          <span className="plugin-picker-description">{skill.description}</span>
          <span className="global-search-tag">{t(skillSourceLabels[skill.source] ?? "Plugin")}</span>
        </div>)}
        {skills && count === 0 && <p className="reference-empty">{t("No skill matches.")}</p>}
      </div>
      <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t("insert")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
    </AppWindowSurface>
  </dialog>;
}
