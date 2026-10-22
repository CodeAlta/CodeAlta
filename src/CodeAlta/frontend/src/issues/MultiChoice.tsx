import { Button, Checkbox, PopoverNext } from "@blueprintjs/core";
import type { IssueFilterValue } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/**
 * The choice of several values of a filter (the states New, Ready and On hold): a button that says what is chosen and
 * opens the values as checkboxes. No value chosen is the choice of every item.
 */
export function MultiChoice({ label, bare, values, chosen, allText, text, className, disabled, onChange }: {
  /** The name of the filter, for the button and its list. */
  label: string;
  /** Whether the button leaves out the name, which a label beside it already says. */
  bare?: boolean;
  values: readonly IssueFilterValue[];
  /** The ids of the values chosen, in any order. */
  chosen: readonly string[];
  /** What the button says when no value is chosen. */
  allText: string;
  /** The text of a value. */
  text: (value: IssueFilterValue) => string;
  className?: string;
  disabled?: boolean;
  onChange: (chosen: string[]) => void;
}) {
  const { t } = useShellLanguage();
  const picked = values.filter(value => chosen.includes(value.id));
  // A value chosen before that the tracker no longer offers still counts until it is removed.
  const unknown = chosen.filter(id => !values.some(value => value.id === id));
  const summary = picked.length === 0 && unknown.length === 0 ? allText
    : `${bare ? "" : `${label}: `}${[...picked.map(text), ...unknown].slice(0, 2).join(", ")}${picked.length + unknown.length > 2 ? ` +${picked.length + unknown.length - 2}` : ""}`;
  const toggle = (id: string, on: boolean) => onChange(on ? [...chosen.filter(other => other !== id), id] : chosen.filter(other => other !== id));
  // The values are given in the order the tracker offers them, whatever the order they were chosen in.
  const ordered = (ids: string[]) => [...values.map(value => value.id).filter(id => ids.includes(id)), ...ids.filter(id => !values.some(value => value.id === id))];
  return <PopoverNext placement="bottom-start" disabled={disabled} content={<div className="issues-multi-popover" role="group" aria-label={label}>
      <div className="issues-multi-list">
        {values.map(value => <Checkbox key={value.id} checked={chosen.includes(value.id)} label={text(value)}
          onChange={event => { const on = event.currentTarget.checked; onChange(ordered(on ? [...chosen, value.id] : chosen.filter(other => other !== value.id))); }} />)}
        {unknown.map(id => <Checkbox key={id} checked label={id} onChange={() => toggle(id, false)} />)}
      </div>
      <Button size="small" variant="minimal" disabled={chosen.length === 0} onClick={() => onChange([])}>{t("Clear the choice")}</Button>
    </div>}>
    <Button size="small" className={`issues-multi${className ? ` ${className}` : ""}`} disabled={disabled} aria-label={`${label}: ${picked.length === 0 && unknown.length === 0 ? allText : [...picked.map(text), ...unknown].join(", ")}`} title={summary}
      endIcon={<AppIcon name="chevronDown" size={14} />} data-chosen={chosen.length > 0 || undefined}>{summary}</Button>
  </PopoverNext>;
}
