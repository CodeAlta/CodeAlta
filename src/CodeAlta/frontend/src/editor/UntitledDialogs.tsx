import { useMemo, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, InputGroup, Menu, MenuItem } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { editorLanguages, type EditorLanguage } from "../monaco/fileLanguage";
import { useShellLanguage } from "../shellLanguage";
import { treeNameProblem } from "./fileTree";

/**
 * Asks where a new file is saved: its path in the project, with `/` between folders. The folders that do not
 * exist yet are created. `problem` is what the host answered to the last try.
 */
export function SaveAsDialog({ name, project, busy, problem, onSave, onCancel }: {
  /** The name proposed first. */
  name: string;
  /** The folder of the project, shown before the path. */
  project: string;
  busy: boolean; problem: MessageKey | null;
  onSave: (path: string) => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const [value, setValue] = useState(name);
  const [touched, setTouched] = useState(false);
  // What the name itself says before the host is asked: empty, a part that is no name of a file.
  const typed = treeNameProblem(value, [], null, true);
  const shown = touched ? typed : problem;
  const path = value.replace(/\\/gu, "/").trim();
  const submit = () => { if (!busy && typed === null) { setTouched(false); onSave(path); } };
  return <Dialog isOpen className="save-as-dialog" title={t("Save as")} isCloseButtonShown={false} canOutsideClickClose={false} onClose={() => { if (!busy) onCancel(); }}>
    <DialogBody>
      <FormGroup label={t("Path in the project")} labelFor="save-as-path" helperText={shown ? t(shown) : project} intent={shown ? "danger" : "none"}>
        <InputGroup id="save-as-path" value={value} disabled={busy} autoFocus spellCheck={false} intent={shown ? "danger" : "none"} placeholder="src/notes.md"
          inputRef={input => {
            // The name is selected without its extension, so that typing replaces it.
            if (input && !input.dataset.selected) { input.dataset.selected = "true"; const dot = name.lastIndexOf("."); input.setSelectionRange(0, dot > 0 ? dot : name.length); }
          }}
          onChange={event => { setValue(event.target.value); setTouched(true); }}
          onKeyDown={event => { if (event.key === "Enter" && !event.nativeEvent.isComposing) { event.preventDefault(); submit(); } }} />
      </FormGroup>
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>
      <Button intent="primary" loading={busy} disabled={typed !== null} onClick={submit}>{t("Save")}</Button>
    </>} />
  </Dialog>;
}

/**
 * Chooses the language a text is shown in: the languages the editor colors, filtered by what is typed. The first
 * entry gives the choice back to the name of the file.
 */
export function LanguageDialog({ current, automatic, names, onChoose, onCancel }: {
  /** The language the text is shown in now. */
  current: EditorLanguage | null;
  /** Whether a language was chosen: only then is there a choice to give back. */
  automatic: boolean;
  /** The name each language is shown with, when the editor knows one. */
  names: (language: EditorLanguage) => string;
  onChoose: (language: EditorLanguage | null) => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const [query, setQuery] = useState("");
  const listed = useMemo(() => {
    const wanted = query.trim().toLowerCase();
    return editorLanguages.map(language => ({ language, name: names(language) }))
      .filter(item => !wanted || item.name.toLowerCase().includes(wanted) || item.language.includes(wanted))
      .sort((left, right) => left.name.localeCompare(right.name, undefined, { sensitivity: "base" }));
  }, [query, names]);
  return <Dialog isOpen className="editor-language-dialog" title={t("Select the language")} onClose={onCancel}>
    <DialogBody>
      <InputGroup value={query} autoFocus spellCheck={false} placeholder={t("Filter languages")}
        aria-label={t("Filter languages")} onChange={event => setQuery(event.target.value)}
        onKeyDown={event => { if (event.key === "Enter" && !event.nativeEvent.isComposing && listed.length > 0) { event.preventDefault(); onChoose(listed[0].language); } }} />
      <Menu className="editor-language-list" aria-label={t("Select the language")}>
        {!query.trim() && !automatic && <MenuItem roleStructure="listoption" icon={<AppIcon name="refresh" size={14} />} text={t("Detect from the file name")} onClick={() => onChoose(null)} />}
        {listed.map(item => <MenuItem key={item.language} roleStructure="listoption" selected={item.language === current} text={item.name}
          label={item.name.toLowerCase() === item.language ? undefined : item.language} onClick={() => onChoose(item.language)} />)}
        {listed.length === 0 && <MenuItem disabled text={t("No language matches.")} />}
      </Menu>
    </DialogBody>
  </Dialog>;
}
