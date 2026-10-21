import { useEffect, useRef, useState } from "react";
import { Button, Card, HTMLSelect, InputGroup } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { preferenceNotice } from "../localization";
import { builtinCodeFont, fontFamilies, installedFontFamilies, promptFonts, withFirstFamily, type LocalFont, type PromptFont } from "./codeFonts";
import { useFontPreferences } from "./fontPreferences";

// What the previews show: the characters a code font tells apart, and the scripts it may not have.
const codeSample = "if (count != 0) { return \"Hello, 世界 · こんにちは\"; } // 0O 1lI";
const promptSample = "Fix the parser — 修复解析器 · パーサーを直す";

type Installed = { monospace: string[]; other: string[] } | "loading" | "unavailable" | null;
type LocalFontAccess = { queryLocalFonts?: () => Promise<readonly LocalFont[]> };

// Whether a family is a monospace one: its narrowest and widest letters take the same room.
function monospaceProbe(): (family: string) => boolean {
  const context = document.createElement("canvas").getContext("2d");
  if (!context) return () => false;
  return family => {
    context.font = `20px "${family}", serif`;
    return Math.abs(context.measureText("iiiiiiii").width - context.measureText("MMMMMMMM").width) < 1;
  };
}

/**
 * The card of Settings → Appearance with the code font and the font of the prompt. The code font is a list of
 * installed fonts the user names, or picks where the browser lists them; the built-in font follows it.
 */
export function FontSettings() {
  const { t, locale } = useShellLanguage();
  const [preferences, update, saved] = useFontPreferences();
  const shown = preferences.code.join(", ");
  const [draft, setDraft] = useState(shown);
  useEffect(() => setDraft(shown), [shown]);
  const [installed, setInstalled] = useState<Installed>(null);
  const picker = useRef<HTMLSelectElement>(null);
  const canList = typeof window !== "undefined" && typeof (window as LocalFontAccess).queryLocalFonts === "function";
  const commit = (text: string) => {
    const code = fontFamilies(text);
    if (code.join(", ") === shown) setDraft(shown); else update({ code });
  };
  const listInstalled = async () => {
    setInstalled("loading");
    try {
      const fonts = await (window as LocalFontAccess).queryLocalFonts!();
      setInstalled(installedFontFamilies(fonts, monospaceProbe(), locale));
      requestAnimationFrame(() => picker.current?.focus());
    } catch { setInstalled("unavailable"); }
  };
  const options = (families: readonly string[]) => families.map(family => <option key={family} value={family}>{family}</option>);
  return <Card className="appearance-settings font-settings" aria-labelledby="settings-fonts-title">
    <h2 id="settings-fonts-title">{t("Fonts")}</h2>
    <Field label={t("Code font")} htmlFor="settings-code-font" notice={<div className="settings-field-wide">
      <p className="settings-field-help">{t("Code blocks, editors and file views. Name installed fonts separated by commas: a character the first one lacks is taken from the next one, then from the built-in {font}.", { font: builtinCodeFont })}</p>
      {installed === "unavailable" && <p role="status" className="notice">{t("The installed fonts could not be listed: type their names.")}</p>}
      {!saved && <p role="status" className="notice" data-diagnostic="unsaved">{preferenceNotice(locale, "Fonts", "", "unsaved")}</p>}
      <pre className="font-settings-preview" aria-label={t("Preview")}>{codeSample}</pre>
    </div>}>
      <InputGroup id="settings-code-font" className="font-settings-families" value={draft} placeholder={builtinCodeFont} spellCheck={false} autoComplete="off"
        onValueChange={setDraft} onBlur={() => commit(draft)}
        onKeyDown={event => {
          if (event.nativeEvent.isComposing) return;
          if (event.key === "Enter") { event.preventDefault(); commit(draft); }
          else if (event.key === "Escape" && draft !== shown) { event.preventDefault(); event.stopPropagation(); setDraft(shown); }
        }} />
      {canList && (typeof installed === "object" && installed
        ? <HTMLSelect ref={picker} aria-label={t("Choose an installed font")} value="" onChange={event => { if (event.target.value) update({ code: withFirstFamily(preferences.code, event.target.value) }); }}>
          <option value="">{t("Choose an installed font")}</option>
          {installed.monospace.length > 0 && <optgroup label={t("Monospace fonts")}>{options(installed.monospace)}</optgroup>}
          {installed.other.length > 0 && <optgroup label={t("Other fonts")}>{options(installed.other)}</optgroup>}
        </HTMLSelect>
        : <Button loading={installed === "loading"} onClick={() => void listInstalled()}>{t("Installed fonts")}</Button>)}
      <Button variant="minimal" disabled={!preferences.code.length && !draft} onClick={() => { setDraft(""); update({ code: [] }); }}>{t("Reset")}</Button>
    </Field>
    <Field label={t("Prompt font")} htmlFor="settings-prompt-font" notice={<div className="settings-field-wide">
      <p className="settings-field-help">{t("The prompt is written in the code font, or in the font of the rest of the window.")}</p>
      <p className="font-settings-preview prompt">{promptSample}</p>
    </div>}>
      <HTMLSelect id="settings-prompt-font" value={preferences.prompt} onChange={event => update({ prompt: event.target.value as PromptFont })}>
        {promptFonts.map(font => <option key={font} value={font}>{t(font === "code" ? "Code font" : "Interface font")}</option>)}
      </HTMLSelect>
    </Field>
  </Card>;
}
