import { useEffect, useRef, useState } from "react";
import { Button, Card, HTMLSelect, InputGroup } from "@blueprintjs/core";
import type { ExternalEditorsResponse, ExternalEditorsSetRequest } from "#neoastra";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { ExternalEditorIcon } from "./externalEditors";

/** What the card asks the host. */
export interface ExternalEditorSettingsApi {
  list(refresh: boolean): Promise<ExternalEditorsResponse>;
  set(request: ExternalEditorsSetRequest): Promise<ExternalEditorsResponse>;
}

const custom = "custom";

// Whether the arguments leave a quote open: two quotes inside quotes are one quote, and count as two.
const openQuote = (text: string) => (text.match(/"/g)?.length ?? 0) % 2 === 1;

/**
 * The editor folders and files are opened in beside the code editor of CodeAlta: the editors found on the machine,
 * the one in use first named by its name, or a program the user names with its arguments.
 */
export function ExternalEditorSettings({ api, onChanged }: { api: ExternalEditorSettingsApi; onChanged?: (reply: ExternalEditorsResponse) => void }) {
  const { t } = useShellLanguage();
  const [reply, setReply] = useState<ExternalEditorsResponse | null>(null);
  const [selected, setSelected] = useState("");
  const [program, setProgram] = useState("");
  const [args, setArgs] = useState("");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const alive = useRef(true);
  function accept(next: ExternalEditorsResponse, keepCustomDraft = false) {
    if (!alive.current) return;
    setReply(next);
    setFailed(next.status !== "ok");
    if (keepCustomDraft && next.chosen !== custom) return;
    setSelected(next.chosen === custom ? custom : next.current?.id ?? "");
    setProgram(next.customProgram ?? "");
    setArgs(next.customArguments ?? "");
  }
  async function run(call: () => Promise<ExternalEditorsResponse>, keepCustomDraft = false) {
    setBusy(true);
    try {
      const next = await call();
      accept(next, keepCustomDraft);
      if (next.status === "ok") onChanged?.(next);
    } catch { if (alive.current) setFailed(true); }
    finally { if (alive.current) setBusy(false); }
  }
  useEffect(() => {
    alive.current = true;
    void run(() => api.list(false));
    return () => { alive.current = false; };
  }, [api]);
  if (!reply || reply.status === "unavailable") return null;
  const editors = reply.editors;
  function choose(id: string) {
    setSelected(id);
    // A custom editor is kept once it has a program.
    if (id === custom) { if (program.trim()) void run(() => api.set({ id: custom, program, arguments: args })); return; }
    void run(() => api.set({ id, program: null, arguments: null }));
  }
  function saveCustom() {
    if (!program.trim() || openQuote(args)) return;
    if (reply?.chosen === custom && program === (reply.customProgram ?? "") && args === (reply.customArguments ?? "")) return;
    void run(() => api.set({ id: custom, program, arguments: args }), true);
  }
  const current = reply.current;
  return <Card className="appearance-settings external-editor-settings" aria-labelledby="settings-external-editor-title">
    <h2 id="settings-external-editor-title">{t("External editor")}</h2>
    <Field setting="general.external_editor" label={t("Editor")} htmlFor="settings-external-editor"
      notice={<p className="settings-field-help">{t("Used by Open in… and /external_editor. The code editor of CodeAlta stays the one that opens files.")}</p>}>
      <span className="external-editor-choice">
        {current && selected !== custom && <ExternalEditorIcon id={current.id} size={16} />}
        <HTMLSelect id="settings-external-editor" value={selected} disabled={busy} onChange={event => choose(event.target.value)}>
          {editors.length === 0 && <option value="">{t("None found")}</option>}
          {editors.map(editor => <option key={editor.id} value={editor.id} title={editor.program}>{t("{editor} (found)", { editor: editor.name })}</option>)}
          <option value={custom}>{t("Custom…")}</option>
        </HTMLSelect>
        <Button variant="minimal" size="small" disabled={busy} onClick={() => void run(() => api.list(true))}>{t("Detect again")}</Button>
      </span>
    </Field>
    {selected === custom && <>
      <Field setting="general.external_editor" label={t("Program")} htmlFor="settings-external-editor-program" notice={<p className="settings-field-help">{t("The full path of the editor's program.")}</p>}>
        <InputGroup id="settings-external-editor-program" value={program} disabled={busy} spellCheck={false} placeholder={reply.platform === "windows" ? "C:\\Program Files\\Editor\\editor.exe" : "/usr/local/bin/editor"}
          onChange={event => setProgram(event.target.value)} onBlur={saveCustom} onKeyDown={event => { if (event.key === "Enter") saveCustom(); }} />
      </Field>
      <Field setting="general.external_editor" label={t("Arguments")} htmlFor="settings-external-editor-arguments"
        notice={<p className={openQuote(args) ? "settings-field-help notice" : "settings-field-help"} role={openQuote(args) ? "status" : undefined}>
          {t(openQuote(args) ? "The arguments have a quote that is not closed." : "{folder}, {file}, {line} and {column} are replaced; an argument with {file} is left out when a folder is opened.")}</p>}>
        <InputGroup id="settings-external-editor-arguments" value={args} disabled={busy} spellCheck={false} placeholder="{folder}"
          onChange={event => setArgs(event.target.value)} onBlur={saveCustom} onKeyDown={event => { if (event.key === "Enter") saveCustom(); }} />
      </Field>
    </>}
    {failed && <p role="status" className="notice">{t("The editor could not be saved.")}</p>}
  </Card>;
}
