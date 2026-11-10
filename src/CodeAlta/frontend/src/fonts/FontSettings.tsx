import { useEffect, useRef, useState } from "react";
import { Button, Card, HTMLSelect, InputGroup } from "@blueprintjs/core";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { preferenceNotice } from "../localization";
import { builtinCodeFont, codeFontSizes, defaultFontPreferences, fontFamilies, installedFontFamilies, interfaceTextSizes, promptFonts, withFirstFamily,
  type LocalFont, type PromptFont } from "./codeFonts";
import { useFontPreferences } from "./fontPreferences";

// What the previews show: the characters a code font tells apart, and the scripts it may not have.
const codeSample = "if (count != 0) { return \"Hello, 世界 · こんにちは\"; } // 0O 1lI";
const promptSample = "Fix the parser — 修复解析器 · パーサーを直す";

// The sizes a list offers: the presets, and the size the window has when an agent chose another one.
const sizeChoices = (presets: readonly number[], value: number) => [...new Set([...presets, value])].sort((a, b) => a - b);

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
 * The card of Settings → Appearance with the code font, the font of the prompt, the size of the text of the code
 * editors and that of the rest of the window. The code font is a list of
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
    <Field setting="appearance.code_font" label={t("Code font")} htmlFor="settings-code-font" notice={<div className="settings-field-wide">
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
    <Field setting="appearance.prompt_font" label={t("Prompt font")} htmlFor="settings-prompt-font" notice={<div className="settings-field-wide">
      <p className="settings-field-help">{t("The prompt is written in the code font, or in the font of the rest of the window.")}</p>
      <p className="font-settings-preview prompt">{promptSample}</p>
    </div>}>
      <HTMLSelect id="settings-prompt-font" value={preferences.prompt} onChange={event => update({ prompt: event.target.value as PromptFont })}>
        {promptFonts.map(font => <option key={font} value={font}>{t(font === "code" ? "Code font" : "Interface font")}</option>)}
      </HTMLSelect>
    </Field>
    <Field setting="appearance.code_font_size" label={t("Code text size")} htmlFor="settings-code-font-size"
      notice={<p className="settings-field-help">{t("The size of the text of the code editor, the diffs of changes and reviews, and the prompt, in pixels.")}</p>}>
      <HTMLSelect id="settings-code-font-size" value={preferences.codeSize} onChange={event => update({ codeSize: Number(event.target.value) })}>
        {sizeChoices(codeFontSizes, preferences.codeSize).map(size => <option key={size} value={size}>
          {size === defaultFontPreferences.codeSize ? t("{name} (default)", { name: `${size} px` }) : `${size} px`}</option>)}
      </HTMLSelect>
    </Field>
    <Field setting="appearance.interface_text_size" label={t("Interface text size")} htmlFor="settings-interface-text-size"
      notice={<p className="settings-field-help">{t("The size of the rest of the text of the window, conversations, lists, menus and dialogs, in percent. The zoom of the window enlarges the text and everything around it.")}</p>}>
      <HTMLSelect id="settings-interface-text-size" value={preferences.interfaceSize} onChange={event => update({ interfaceSize: Number(event.target.value) })}>
        {sizeChoices(interfaceTextSizes, preferences.interfaceSize).map(size => {
          const text = t("{percent}%", { percent: size.toLocaleString(locale) });
          return <option key={size} value={size}>{size === defaultFontPreferences.interfaceSize ? t("{name} (default)", { name: text }) : text}</option>;
        })}
      </HTMLSelect>
    </Field>
  </Card>;
}
