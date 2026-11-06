import { Checkbox, HTMLSelect, InputGroup, NumericInput, Switch } from "@blueprintjs/core";
import type { AutomationTriggerFieldItem, AutomationTriggerItem, AutomationTriggerTypeItem } from "#neoastra";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { secretName, secretReference, sourceSettings, withSourceEvents, withSourceSetting, type SourceValue } from "./automations";

// The words of the events and fields of the sources of CodeAlta; those of a plugin are its own.
const builtInLabels: Readonly<Record<string, MessageKey>> = {
  "project.added": "Added", "project.archived": "Archived", "project.removed": "Removed",
  "automation.completed": "Completed", "automation.failed": "Failed", "automation.waiting": "Waits for an approval",
  "project.space": "Space", "automation.automations": "Automations",
};

/**
 * The events and the settings of the trigger of a source, drawn from what its source says of them: the events it starts on,
 * several at once, and a field for each setting by its kind. A secret is written by its name, as a reference.
 */
export function SourceTriggerFields({ trigger, source, disabled, onChange }: {
  trigger: AutomationTriggerItem;
  /** What the source says of its trigger; null while it is not known (a plugin that is not running). */
  source: AutomationTriggerTypeItem | null;
  disabled: boolean;
  onChange: (value: AutomationTriggerItem) => void;
}) {
  const { t } = useShellLanguage();
  const label = (key: string, text: string) => builtInLabels[`${trigger.type}.${key}`] ? t(builtInLabels[`${trigger.type}.${key}`]!) : text;
  if (!source) return <span className="automation-source-missing">{trigger.type}</span>;
  const on = trigger.source?.on ?? [];
  const settings = sourceSettings(trigger);
  const set = (key: string, value: SourceValue | null) => onChange(withSourceSetting(trigger, key, value));
  return <>
    <span className="automation-source-events" role="group" aria-label={t("Events")}>
      {source.events.map(event => <Checkbox key={event.id} inline disabled={disabled} label={label(event.id, event.label)} title={event.description ?? undefined}
        checked={on.length === 0 ? event.id === source.events[0]?.id : on.includes(event.id)}
        onChange={changed => {
          const current = on.length === 0 && source.events[0] ? [source.events[0].id] : on;
          onChange(withSourceEvents(trigger, changed.currentTarget.checked ? [...current, event.id] : current.filter(id => id !== event.id)));
        }} />)}
    </span>
    {source.fields.map(field => <SourceField key={field.id} field={field} label={label(field.id, field.label)} value={settings[field.id] ?? null} disabled={disabled}
      onChange={value => set(field.id, value)} />)}
  </>;
}

function SourceField({ field, label, value, disabled, onChange }: {
  field: AutomationTriggerFieldItem; label: string; value: SourceValue | null; disabled: boolean; onChange: (value: SourceValue | null) => void;
}) {
  const { t } = useShellLanguage();
  const text = typeof value === "string" ? value : Array.isArray(value) ? value.join(", ") : "";
  const title = field.description ?? undefined;
  switch (field.kind) {
    case "flag":
      return <Switch inline checked={value === true} disabled={disabled} label={label} title={title} onChange={event => onChange(event.currentTarget.checked ? true : null)} />;
    case "number":
      return <NumericInput className="automation-source-number" value={typeof value === "number" ? value : ""} disabled={disabled} buttonPosition="none" aria-label={label}
        title={title} placeholder={field.placeholder ?? label} onValueChange={number => onChange(Number.isInteger(number) ? number : null)} />;
    case "choice":
      return <HTMLSelect value={text} disabled={disabled} aria-label={label} title={title} onChange={event => onChange(event.target.value || null)}>
        <option value="">{field.required ? label : `${label}: —`}</option>
        {field.choices.map(choice => <option key={choice.id} value={choice.id}>{choice.label}</option>)}
      </HTMLSelect>;
    case "choices": {
      const chosen = Array.isArray(value) ? value : typeof value === "string" ? [value] : [];
      return <span className="automation-source-choices" role="group" aria-label={label} title={title}>
        {field.choices.map(choice => <Checkbox key={choice.id} inline disabled={disabled} label={choice.label} checked={chosen.includes(choice.id)}
          onChange={event => onChange(event.currentTarget.checked ? [...chosen, choice.id] : chosen.filter(id => id !== choice.id))} />)}
      </span>;
    }
    case "secret":
      return <InputGroup className="automation-source-text" value={secretName(text)} disabled={disabled} spellCheck={false} aria-label={label} title={title}
        placeholder={`${label}: ${t("Name of the secret")}`} onChange={event => onChange(secretReference(event.target.value))} />;
    case "automations":
      return <InputGroup className="automation-source-text" value={text} disabled={disabled} spellCheck={false} aria-label={label} title={title ?? t("Names or ids, separated by commas")}
        placeholder={`${label}: ${t("Any other automation")}`}
        onChange={event => { const names = event.target.value.split(",").map(name => name.trim()); onChange(names.some(Boolean) ? names : null); }}
        onBlur={() => { if (Array.isArray(value)) onChange(value.filter(name => name.trim())); }} />;
    default:
      return <InputGroup className="automation-source-text" value={text} disabled={disabled} spellCheck={false} aria-label={label} title={title}
        placeholder={field.kind === "space" && !field.placeholder ? `${label}: ${t("Every space")}` : field.placeholder ? `${label}: ${field.placeholder}` : label}
        onChange={event => onChange(event.target.value || null)} />;
  }
}
