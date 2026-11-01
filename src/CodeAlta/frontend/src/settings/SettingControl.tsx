import { useEffect, useRef, useState } from "react";
import { Button, HTMLSelect, InputGroup, NumericInput, Switch, TagInput } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { choiceText, type SettingChoice, type SettingEntry } from "./settingsCatalog";
import type { SettingValueSource } from "./settingValues";

/** Whether the search can change a setting where it shows it: a value of one of the simple kinds, there, and changed outside its page. */
export const editableInPlace = (entry: SettingEntry) => entry.available && entry.writable
  && ["boolean", "choice", "integer", "number", "text", "path", "list"].includes(entry.kind);

type State = Readonly<{ status: "loading" | "ready" | "failed"; value: unknown; choices: readonly SettingChoice[] | null; message: string | null }>;

/**
 * The control of a setting drawn from its descriptor, for the results of the search of Settings and the generated page
 * of a plugin: a switch, a list of choices, a number, a text or a list of texts, saved at once through the same binding
 * as the page of the setting. `revision` changes when a value may have changed elsewhere, and the value is read again.
 */
export function SettingControl({ entry, source, revision, label }: { entry: SettingEntry; source: SettingValueSource; revision?: unknown; label: string }) {
  const { t, locale } = useShellLanguage();
  const [state, setState] = useState<State>({ status: "loading", value: null, choices: null, message: null });
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    let current = true;
    void source.get(entry).then(read => {
      if (!current) return;
      const choices = read.choices ? read.choices.map(choice => ({ value: choice.value, label: choice.label, literal: true })) : null;
      setState(read.status === "ok" ? { status: "ready", value: read.value, choices, message: null } : { status: "failed", value: null, choices: null, message: read.message });
    });
    return () => { current = false; };
  }, [entry, source, revision]);

  async function save(value: unknown) {
    setBusy(true);
    const reply = await source.set(entry, value);
    if (!alive.current) return;
    setBusy(false);
    setDraft(null);
    setState(current => reply.status === "ok"
      ? { ...current, status: "ready", value: reply.value, message: null }
      : { ...current, message: reply.message ?? t("The value was not accepted.") });
  }

  if (state.status === "loading") return <span className="setting-control-placeholder" aria-busy="true" />;
  if (state.status === "failed") return <span className="settings-field-help">{t("Not available here")}</span>;
  const disabled = busy;
  const choices = entry.choices.length ? entry.choices : state.choices ?? [];
  const isDefault = JSON.stringify(state.value) === JSON.stringify(entry.defaultValue);
  const reset = entry.defaultValue !== null && !isDefault && entry.kind !== "list"
    ? <Button variant="minimal" size="small" disabled={disabled} onClick={() => void save(undefined)} title={t("Back to its default")} aria-label={t("Back to its default")}>{t("Reset")}</Button>
    : null;
  const message = state.message && <span className="settings-field-help error-text" role="alert">{state.message}</span>;
  let control: React.ReactNode;
  switch (entry.kind) {
    case "boolean":
      control = <Switch className="settings-checkbox" checked={state.value === true} disabled={disabled} aria-label={label}
        onChange={event => void save(event.currentTarget.checked)} />;
      break;
    case "choice":
      control = <HTMLSelect value={typeof state.value === "string" ? state.value : ""} disabled={disabled} aria-label={label} onChange={event => void save(event.target.value)}>
        {typeof state.value === "string" && !choices.some(choice => choice.value === state.value) && <option value={state.value}>{state.value}</option>}
        {choices.map(choice => <option key={choice.value} value={choice.value}>{choiceText(locale, choice, entry.builtIn)}</option>)}
      </HTMLSelect>;
      break;
    case "integer":
    case "number": {
      const text = draft ?? (typeof state.value === "number" ? String(state.value) : "");
      const commit = (typed: string) => {
        const number = Number(typed.trim());
        const valid = typed.trim() !== "" && Number.isFinite(number) && (entry.kind === "number" || Number.isInteger(number))
          && (entry.minimum === null || number >= entry.minimum) && (entry.maximum === null || number <= entry.maximum);
        if (valid && number !== state.value) void save(number); else setDraft(null);
      };
      control = <NumericInput className="setting-control-number" value={text} min={entry.minimum ?? undefined} max={entry.maximum ?? undefined} stepSize={1} minorStepSize={null}
        clampValueOnBlur={false} buttonPosition="right" size="small" disabled={disabled} aria-label={label}
        onValueChange={(_value, typed) => setDraft(typed)} onButtonClick={(_value, typed) => commit(typed)} onBlur={event => commit(event.currentTarget.value)}
        onKeyDown={event => { if (event.key === "Enter") commit(event.currentTarget.value); if (event.key === "Escape" && draft !== null) { event.stopPropagation(); setDraft(null); } }} />;
      break;
    }
    case "text":
    case "path": {
      const text = draft ?? (typeof state.value === "string" ? state.value : "");
      const commit = () => { if (draft !== null && draft.trim() !== state.value) void save(draft.trim()); else setDraft(null); };
      control = <InputGroup className="setting-control-text" value={text} disabled={disabled} aria-label={label} size="small"
        onValueChange={setDraft} onBlur={commit}
        onKeyDown={event => { if (event.key === "Enter") commit(); if (event.key === "Escape" && draft !== null) { event.stopPropagation(); setDraft(null); } }} />;
      break;
    }
    case "list":
      control = <TagInput className="setting-control-list" values={Array.isArray(state.value) ? state.value.map(String) : []} disabled={disabled} inputProps={{ "aria-label": label }}
        addOnBlur onChange={values => void save(values.map(String).map(value => value.trim()).filter(Boolean))} />;
      break;
    default:
      control = null;
  }
  return <>{control}{reset}{message}</>;
}
