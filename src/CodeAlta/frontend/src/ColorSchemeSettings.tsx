import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Button, Callout, InputGroup, PopoverNext, SegmentedControl } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import type { AppearancePreviewStore } from "./appearancePreview";
import { ColorSchemeSelect } from "./ColorSchemeSelect";
import type { ColorSchemeLibrary } from "./colorSchemeLibrary";
import { parseColor, schemeColorNames, type SchemeColorName } from "./colorPalette";
import { colorSchemeOf, colorVariants, maximumSchemeNameLength, type ColorScheme, type ColorVariant, type CustomColorScheme } from "./colorSchemes";
import { copyName, draftScheme, sameScheme, schemeNameProblem, shownColors, withColor } from "./customColorSchemes";
import type { MessageKey } from "./localization";
import { SettingsField } from "./SettingsField";
import { settingsFailure, type SettingsNotice } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";

const colorLabels: Readonly<Record<SchemeColorName, MessageKey>> = {
  background: "Background", text: "Text", muted: "Muted text", accent: "Accent", success: "Success", warning: "Warning", danger: "Danger" };
const variantLabels: Readonly<Record<ColorVariant, MessageKey>> = { light: "Light", dark: "Dark", darker: "Darker" };

const noSchemes: readonly CustomColorScheme[] = Object.freeze([]);

/** What is being edited: the scheme as it is now, the saved scheme it started from (none for a new one), and the theme shown. */
type Editing = Readonly<{ draft: CustomColorScheme; original: CustomColorScheme | null; variant: ColorVariant }>;

// One color of a scheme: a well that opens the color picker of the system, and the same color as text.
function ColorField({ label, value, chosen, disabled, onChange, onReset }: {
  label: string; value: string; chosen: boolean; disabled: boolean; onChange: (color: string) => void; onReset: () => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  // What is being typed, while it is not a color yet.
  const [typed, setTyped] = useState<string | null>(null);
  return <div className="scheme-color" data-chosen={chosen || undefined}>
    <label htmlFor={id}>{label}</label>
    <input id={id} type="color" className="scheme-color-well" value={value} disabled={disabled} onChange={event => { setTyped(null); onChange(event.target.value); }} />
    <InputGroup className="scheme-color-text" value={typed ?? value} maxLength={7} spellCheck={false} disabled={disabled} aria-label={`${label} (#rrggbb)`}
      onChange={event => {
        const text = event.target.value;
        setTyped(text);
        // A color pasted without its sign is a color too.
        const color = parseColor(text) ?? parseColor(`#${text}`);
        if (color) onChange(color);
      }} onBlur={() => setTyped(null)} />
    <Button variant="minimal" size="small" className="scheme-color-reset" icon={<AppIcon name="reset" size={14} />} disabled={disabled || !chosen}
      aria-label={t("Reset {name}", { name: label })} title={t("Reset {name}", { name: label })} onClick={() => { setTyped(null); onReset(); }} />
  </div>;
}

/**
 * The color scheme of the window: the dropdown that selects one and, for the user's own schemes, their
 * editor. A scheme is made from another (a built-in one, or one of the user's) by choosing some of its
 * colors; the window shows the scheme while it is edited, and nothing is kept until it is saved.
 */
export function ColorSchemeSettings({ colorScheme, setColorScheme, shownScheme, variant, customSchemes, library, preview, platform, notice: storageNotice }: {
  /** The selection, and the scheme it stands for. */
  colorScheme: string; setColorScheme: (selection: string) => void; shownScheme: ColorScheme | CustomColorScheme;
  /** The variant the theme shows. */
  variant: ColorVariant;
  /** The user's schemes; null until the host has listed them. */
  customSchemes: readonly CustomColorScheme[] | null;
  library: ColorSchemeLibrary; preview: AppearancePreviewStore;
  /** The system, which names its file manager; null where a file cannot be shown. */
  platform: string | null;
  /** The notice about keeping the selection, if any. */
  notice?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const nameId = useId();
  const [editing, setEditing] = useState<Editing | null>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const custom = customSchemes ?? noSchemes;
  const available = library.status === "ok" && customSchemes !== null;

  // The window shows what is being edited, and its own appearance again once the editor is gone.
  useEffect(() => { preview.set(editing ? { scheme: editing.draft, variant: editing.variant } : null); }, [editing, preview]);
  useEffect(() => () => preview.set(null), [preview]);
  // The theme changed under the editor (the switch of the title bar, the system): it shows that theme.
  useEffect(() => { setEditing(current => current && current.variant !== variant ? { ...current, variant } : current); }, [variant]);
  // Files written by hand are read again when the window comes back to the front.
  const { reload } = library;
  useEffect(() => {
    const refresh = () => void reload();
    window.addEventListener("focus", refresh);
    return () => window.removeEventListener("focus", refresh);
  }, [reload]);

  function begin(draft: CustomColorScheme, original: CustomColorScheme | null, shown = variant) {
    setNotice(null);
    setEditing({ draft, original, variant: shown });
  }
  // A built-in scheme is customized as a new scheme based on it; one of the user's is edited itself.
  function customize() {
    if ("base" in shownScheme) begin(shownScheme, shownScheme);
    else begin(draftScheme(shownScheme, copyName(shownScheme.name, name => t("{name} copy", { name }), custom)), null);
  }
  const change = (draft: CustomColorScheme) => setEditing(current => current && { ...current, draft });
  // The editor goes, and the keyboard is back where it opened from.
  function close() {
    setEditing(null);
    requestAnimationFrame(() => trigger.current?.focus());
  }

  const problem = editing ? schemeNameProblem(editing.draft, custom) : null;
  const dirty = !!editing && (!editing.original || !sameScheme(editing.draft, editing.original));

  async function run(action: () => Promise<{ status: string; message?: string | null }>, success: MessageKey) {
    setBusy(true); setNotice(null);
    try {
      const result = await action();
      const failure = settingsFailure(result.status, result.message);
      setNotice(failure ?? { key: success, intent: "success" });
      if (!failure) close();
    } catch {
      setNotice({ key: "The operation did not complete.", intent: "danger" });
    } finally { setBusy(false); }
  }
  async function reveal(id: string | null) {
    try { if ((await library.reveal(id)).status !== "ok") setNotice({ key: "The file manager could not be opened.", intent: "warning" }); }
    catch { setNotice({ key: "The file manager could not be opened.", intent: "warning" }); }
  }

  // The schemes could not be read: the built-in ones remain, and the reason is said once.
  const shownNotice = notice ?? (library.status === "read_failed" ? settingsFailure("read_failed") : null);
  const canSave = !!editing && !busy && !problem && dirty;
  const save = () => { if (editing && canSave) void run(() => library.save(editing.draft), "Saved."); };
  // Escape leaves the editor, not Settings; a popover of the editor (the confirmation) has used its own.
  function leaveOnEscape(event: KeyboardEvent<HTMLElement>) {
    if (event.key !== "Escape" || event.defaultPrevented || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || busy) return;
    event.preventDefault();
    event.stopPropagation();
    close();
  }
  const revealLabel = platform === null ? null : t(platform === "windows" ? "Reveal in File Explorer" : platform === "macos" ? "Reveal in Finder" : "Open containing folder");
  const shown = editing ? shownColors(editing.draft, editing.variant) : null;

  return <SettingsField label={t("Color scheme")} htmlFor="settings-color-scheme" notice={<>
    {storageNotice}
    {shownNotice && <Callout className="settings-field-wide" intent={shownNotice.intent} compact role={shownNotice.intent === "success" ? "status" : "alert"}>
      {t(shownNotice.key)}{shownNotice.detail && <div className="config-editor-diagnostic">{shownNotice.detail}</div>}</Callout>}
    {library.problems.length > 0 && <Callout className="settings-field-wide scheme-problems" intent="warning" compact>
      {t("These files of the color scheme folder are not color schemes:")}
      <ul>{library.problems.map(entry => <li key={entry.file}><code>{entry.file}</code> {entry.message}</li>)}</ul>
    </Callout>}
    {editing && shown && <section className="settings-field-wide scheme-editor" aria-label={t(editing.original ? "Edit color scheme" : "New color scheme")} onKeyDown={leaveOnEscape}>
      <header className="scheme-editor-heading">
        <strong>{t(editing.original ? "Edit color scheme" : "New color scheme")}</strong>
        <span>{t("Based on {name}", { name: colorSchemeOf(editing.draft.base).name })}</span>
      </header>
      <div className="scheme-editor-row">
        <label htmlFor={nameId}>{t("Name")}</label>
        <InputGroup id={nameId} className="scheme-editor-name" value={editing.draft.name} maxLength={maximumSchemeNameLength} disabled={busy} autoFocus
          // A new scheme starts with a proposed name, ready to be typed over.
          onFocus={event => { if (!editing.original) event.target.select(); }}
          onKeyDown={event => { if (event.key === "Enter" && !event.nativeEvent.isComposing) save(); }}
          onChange={event => change({ ...editing.draft, name: event.target.value })} />
      </div>
      <div className="scheme-editor-row">
        <span id={`${nameId}-theme`}>{t("Theme to edit")}</span>
        <SegmentedControl size="small" aria-labelledby={`${nameId}-theme`} disabled={busy} value={editing.variant}
          options={colorVariants.map(value => ({ label: t(variantLabels[value]), value }))}
          onValueChange={value => setEditing(current => current && { ...current, variant: value as ColorVariant })} />
      </div>
      <div className="scheme-editor-colors">
        {schemeColorNames.map(name => <ColorField key={`${editing.variant}:${name}`} label={t(colorLabels[name])} value={shown[name]} disabled={busy}
          chosen={editing.draft[editing.variant][name] !== undefined}
          onChange={color => change(withColor(editing.draft, editing.variant, name, color))}
          onReset={() => change(withColor(editing.draft, editing.variant, name, null))} />)}
      </div>
      <p className="scheme-editor-help">{t("The window shows this scheme while you edit it.")}{editing.variant === "darker" && ` ${t("Colors you do not choose here follow the dark theme.")}`}</p>
      <footer className="scheme-editor-footer">
        {problem && <span className="settings-editor-problem" role="alert">{t(problem)}</span>}
        <Button intent="primary" disabled={!canSave} onClick={save}>{t("Save")}</Button>
        <Button disabled={busy} onClick={close}>{t("Cancel")}</Button>
        <span className="settings-editor-spacer" />
        {editing.original && <>
          <Button icon={<AppIcon name="copy" size={15} />} disabled={busy}
            onClick={() => begin(draftScheme(editing.draft, copyName(editing.draft.name, name => t("{name} copy", { name }), custom)), null, editing.variant)}>{t("Duplicate")}</Button>
          {revealLabel && <Button icon={<AppIcon name="openExternal" size={15} />} disabled={busy} onClick={() => void reveal(editing.original!.id)}>{revealLabel}</Button>}
          <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name}?", { name: editing.original.name })}</p>
            <Button intent="danger" disabled={busy} onClick={() => void run(async () => {
              const result = await library.remove(editing.original!.id);
              // Already gone is removed too. The window goes back to the scheme this one was made from.
              if (result.status !== "ok" && result.status !== "not_found") return result;
              setColorScheme(colorSchemeOf(editing.original!.base).id);
              return { status: "ok" };
            }, "Removed.")}>{t("Remove")}</Button></div>}>
            <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} text={t("Remove")} />
          </PopoverNext>
        </>}
      </footer>
    </section>}
  </>}>
    <ColorSchemeSelect id="settings-color-scheme" value={colorScheme} shown={shownScheme} custom={custom} variant={variant} disabled={!!editing}
      onChange={setColorScheme} onOpening={() => void reload()} />
    {available && <Button ref={trigger} className="scheme-customize" icon={<AppIcon name={"base" in shownScheme ? "edit" : "palette"} size={15} />} disabled={!!editing}
      title={"base" in shownScheme ? undefined : t("Make your own color scheme from this one")} onClick={customize}>{t("base" in shownScheme ? "Edit" : "Customize")}</Button>}
  </SettingsField>;
}
