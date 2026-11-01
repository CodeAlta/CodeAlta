import { useRef, useState, type ReactNode } from "react";
import { Button, HTMLSelect, Switch } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { preferenceNotice } from "../localization";
import { useTimelineEmphasis, useTimelineEmphasisIssue } from "./useTimelineEmphasis";
import { backgroundTintChoices, defaultTimelineEmphasis, entryAppearance, opacityChoices, timelineEntryTypeLabel,
  timelineEntryTypes, type EntryAppearanceState, type EntryEmphasis, type TimelineEmphasis } from "./timelineEmphasis";
import { TimelineAppearancePreview } from "./TimelineAppearancePreview";

/**
 * The row of Settings → Appearance that enables per-type timeline appearance and opens its customization window.
 */
export function TimelineEmphasisSetting() {
  const { t, locale } = useShellLanguage();
  const [emphasis, setEmphasis] = useTimelineEmphasis();
  const issue = useTimelineEmphasisIssue();
  const notice = issue && <p role="status" className="notice" data-diagnostic={issue}>{preferenceNotice(locale, "Entry appearance", "", issue)}</p>;
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const close = () => {
    setOpen(false);
    requestAnimationFrame(() => trigger.current?.focus());
  };
  return <Field setting="appearance.entry_appearance" label={t("Entry appearance")} htmlFor="settings-dim-intermediate" notice={<><p className="settings-field-help">{t("Adjust opacity, background, borders and elevation by entry type.")}</p>{notice}</>}>
    <Switch id="settings-dim-intermediate" className="settings-checkbox" checked={emphasis.enabled} onChange={event => setEmphasis({ ...emphasis, enabled: event.currentTarget.checked })} />
    <Button ref={trigger} icon={<AppIcon name="palette" size={15} />} disabled={!emphasis.enabled} aria-haspopup="dialog" aria-expanded={open} onClick={() => setOpen(true)}>{t("Customize")}</Button>
    {open && <TimelineEmphasisWindow emphasis={emphasis} setEmphasis={setEmphasis} notice={notice} onClose={close} />}
  </Field>;
}

/**
 * Per-type Normal and Hover/focus settings, with production-rendered, isolated examples of the selected type.
 */
function TimelineEmphasisWindow({ emphasis, setEmphasis, notice, onClose }: { emphasis: TimelineEmphasis; setEmphasis: (value: TimelineEmphasis) => void; notice?: ReactNode; onClose: () => void }) {
  const { t } = useShellLanguage();
  const [type, setType] = useState<typeof timelineEntryTypes[number]>("tool");
  const value = emphasis.types[type], appearance = entryAppearance(value), label = t(timelineEntryTypeLabel[type]);
  const change = (patch: Partial<EntryEmphasis>) =>
    setEmphasis({ ...emphasis, types: { ...emphasis.types, [type]: { ...value, ...patch } } });
  const paint = (state: "normal" | "hover", patch: Partial<EntryAppearanceState>) =>
    change({ appearance: { ...appearance, [state]: { ...appearance[state], ...patch } } });
  const opacity = value.dim ? value.opacity : 100, hoverOpacity = value.dim ? Math.max(value.opacity, value.hoverOpacity) : 100;
  const unchanged = JSON.stringify(emphasis.types) === JSON.stringify(defaultTimelineEmphasis.types);
  const typeUnchanged = JSON.stringify(value) === JSON.stringify(defaultTimelineEmphasis.types[type]);
  const percentageOptions = (choices: readonly number[], minimum = 0) => choices.map(choice =>
    <option key={choice} value={choice} disabled={choice < minimum}>{t("{percent}%", { percent: choice })}</option>);
  const paintSelect = (state: "normal" | "hover", property: "outerBorder" | "elevation") => <HTMLSelect
    value={String(appearance[state][property])} aria-label={t(state === "normal" ? "{property} of {type}" : "{property} of {type} on hover or focus", { property: t(property === "outerBorder" ? "Outer border" : "Elevation"), type: label })}
    onChange={event => paint(state, property === "outerBorder" ? { outerBorder: event.target.value === "true" } : { elevation: event.target.value as "none" | "subtle" })}>
    {property === "outerBorder" ? <><option value="false">{t("Hidden")}</option><option value="true">{t("Visible")}</option></>
      : <><option value="none">{t("None")}</option><option value="subtle">{t("Subtle")}</option></>}
  </HTMLSelect>;
  return <AppWindow storageKey="codealta.desktop.window.timelineAppearance.v1" className="timeline-emphasis-dialog" titleId="timeline-emphasis-title"
    title={t("Entry appearance")}
    preferredSize={viewport => ({ width: Math.min(1000, viewport.width - 40), height: Math.min(850, viewport.height - 40) })} minimumSize={{ width: 360, height: 320 }}
    onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); }
    }}>
    {notice}
    <div className="timeline-appearance-layout">
      <nav className="timeline-appearance-types" aria-label={t("Entry types")}>
        <span className="settings-field-help">{t("Entry type")}</span>
        {timelineEntryTypes.map(entryType => <Button key={entryType} minimal active={entryType === type} aria-pressed={entryType === type}
          onClick={() => setType(entryType)}>{t(timelineEntryTypeLabel[entryType])}</Button>)}
      </nav>
      <div className="timeline-appearance-editor">
        <HTMLSelect className="timeline-appearance-type-select" value={type} aria-label={t("Entry type")} onChange={event => setType(event.target.value as typeof type)}>
          {timelineEntryTypes.map(entryType => <option key={entryType} value={entryType}>{t(timelineEntryTypeLabel[entryType])}</option>)}
        </HTMLSelect>
        <h3>{label}</h3>
        <p className="settings-field-help">{t("Type and status colors stay unchanged and follow the active color scheme.")}</p>
        <table className="timeline-appearance-properties">
          <colgroup><col className="timeline-appearance-property" /><col /><col /></colgroup>
          <thead><tr><th scope="col">{t("Appearance")}</th><th scope="col">{t("Normal")}</th><th scope="col">{t("Hover / keyboard focus")}</th></tr></thead>
          <tbody>
            <tr><th scope="row">{t("Content opacity")}</th>
              <td><HTMLSelect value={opacity} aria-label={t("Opacity of {type}", { type: label })} onChange={event => change({ dim: true, opacity: Number(event.target.value) })}>{percentageOptions(opacityChoices)}</HTMLSelect></td>
              <td><HTMLSelect value={hoverOpacity} aria-label={t("Opacity of {type} on hover or focus", { type: label })} onChange={event => change({ dim: true, opacity, hoverOpacity: Number(event.target.value) })}>{percentageOptions(opacityChoices, opacity)}</HTMLSelect></td></tr>
            <tr><th scope="row">{t("Background tint")}</th>{(["normal", "hover"] as const).map(state => <td key={state}><HTMLSelect value={appearance[state].backgroundTint}
              aria-label={t(state === "normal" ? "{property} of {type}" : "{property} of {type} on hover or focus", { property: t("Background tint"), type: label })}
              onChange={event => paint(state, { backgroundTint: Number(event.target.value) })}>{percentageOptions(backgroundTintChoices)}</HTMLSelect></td>)}</tr>
            <tr><th scope="row">{t("Outer border")}</th><td>{paintSelect("normal", "outerBorder")}</td><td>{paintSelect("hover", "outerBorder")}</td></tr>
            <tr><th scope="row">{t("Elevation")}</th><td>{paintSelect("normal", "elevation")}</td><td>{paintSelect("hover", "elevation")}</td></tr>
          </tbody>
        </table>
        {type !== "tool" && <>
          <Switch className="settings-checkbox" label={t("Dim left accent with content")} checked={appearance.dimAccent} onChange={event => change({ appearance: { ...appearance, dimAccent: event.currentTarget.checked } })} />
          <p className="settings-field-help">{t("The left accent stays visible. Hover and focus never lower content opacity.")}</p>
        </>}
        <TimelineAppearancePreview type={type} emphasis={emphasis} />
        <p className="settings-field-help">{t("Errors, warnings, requests and failed or canceled operations keep their original appearance.")}</p>
      </div>
    </div>
    <footer><span><Button disabled={typeUnchanged} onClick={() => setEmphasis({ ...emphasis, types: { ...emphasis.types, [type]: defaultTimelineEmphasis.types[type] } })}>{t("Reset type")}</Button>
      <Button disabled={unchanged} onClick={() => setEmphasis({ ...emphasis, types: defaultTimelineEmphasis.types })}>{t("Reset all")}</Button></span>
      <span><Button intent="primary" onClick={onClose}>{t("Close")}</Button></span></footer>
  </AppWindow>;
}
