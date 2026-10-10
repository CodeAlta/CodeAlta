import { Button, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import { useMemo, useState } from "react";
import { AppIcon } from "../AppIcon";
import { useText } from "./text";
import { filterChoices, ignoredKinds, unsetFilters, type FilterChoice } from "./filters";
import { allowedFrequencies, encodeFrame, filterKeys, periodPresets, resolvePeriod, resolveFrequency, type FilterKey, type PeriodChoice } from "./frame";
import { readMoreChoices } from "./history";
import { comparisonLabel, filterKindLabel, filterValueLabel, frequencyLabel, periodLabel } from "./labels";
import { useStatistics } from "./runtime";
import { queryKey, useStatisticsQuery } from "./useQuery";
import type { Comparison, HistoryChoice, RequestFrequency } from "./types";

// The bar every page shares: period, frequency, comparison, the filters as chips, "Reset" and the menu of the canvas.

const frequencies: readonly RequestFrequency[] = ["auto", "hour", "day", "week", "month", "year"];
const comparisons: readonly Comparison[] = ["none", "previousPeriod", "samePeriodLastYear"];

function PeriodPicker() {
  const { t } = useText();
  const { frame, dispatch, today, header, fmt } = useStatistics();
  const [open, setOpen] = useState(false);
  const custom = frame.period.kind === "custom" ? frame.period : null;
  const [from, setFrom] = useState(custom?.from ?? "");
  const [to, setTo] = useState(custom?.to ?? today);
  const range = frame.period.kind === "preset" && frame.period.preset === "all" && header ? { from: header.from, to: header.to } : resolvePeriod(frame.period, today);
  const valid = /^\d{4}-\d{2}-\d{2}$/.test(from) && /^\d{4}-\d{2}-\d{2}$/.test(to) && from <= to;
  const choose = (period: PeriodChoice) => { dispatch({ type: "period", period }); setOpen(false); };
  const label = frame.period.kind === "preset" ? periodLabel(t, frame.period.preset) : t("Custom range");
  return <PopoverNext placement="bottom-start" isOpen={open} onInteraction={next => setOpen(next)} content={<div className="stats-popover">
    <Menu>
      {periodPresets.map(preset => <MenuItem key={preset} text={periodLabel(t, preset)} roleStructure="listoption" selected={frame.period.kind === "preset" && frame.period.preset === preset}
        labelElement={frame.period.kind === "preset" && frame.period.preset === preset ? <AppIcon name="check" size={14} /> : undefined} onClick={() => choose({ kind: "preset", preset })} shouldDismissPopover={false} />)}
      <MenuDivider title={t("Custom range")} />
    </Menu>
    <form className="stats-range" onSubmit={event => { event.preventDefault(); if (valid) choose({ kind: "custom", from, to }); }}>
      <label><span>{t("From")}</span><input type="date" value={from} max={to || today} onChange={event => setFrom(event.target.value)} /></label>
      <label><span>{t("To")}</span><input type="date" value={to} min={from || undefined} max={today} onChange={event => setTo(event.target.value)} /></label>
      <Button type="submit" size="small" intent="primary" disabled={!valid}>{t("Apply")}</Button>
    </form>
  </div>}>
    <Button size="small" className="stats-control" icon={<AppIcon name="calendar" size={14} />} endIcon={<AppIcon name="chevronDown" size={13} />} aria-label={t("Period: {value}", { value: label })} title={t("Period")}>
      <span className="stats-control-main">{label}</span>{range && <span className="stats-control-sub">{fmt.range(range.from, range.to)}</span>}
    </Button>
  </PopoverNext>;
}

function FrequencyPicker() {
  const { t } = useText();
  const { frame, dispatch, periodDays } = useStatistics();
  const allowed = allowedFrequencies(periodDays);
  const used = resolveFrequency(frame.frequency, periodDays);
  const label = frame.frequency === "auto" ? t("Auto: {value}", { value: frequencyLabel(t, used) }) : frequencyLabel(t, frame.frequency);
  return <PopoverNext placement="bottom-start" content={<Menu>
    {frequencies.map(frequency => <MenuItem key={frequency} text={frequency === "auto" ? t("Auto: {value}", { value: frequencyLabel(t, used) }) : frequencyLabel(t, frequency)} roleStructure="listoption"
      disabled={frequency !== "auto" && !allowed.includes(frequency)} selected={frame.frequency === frequency}
      labelElement={frame.frequency === frequency ? <AppIcon name="check" size={14} /> : undefined} onClick={() => dispatch({ type: "frequency", frequency })} />)}
  </Menu>}>
    <Button size="small" className="stats-control" endIcon={<AppIcon name="chevronDown" size={13} />} aria-label={t("Frequency: {value}", { value: label })} title={t("Frequency")}>{label}</Button>
  </PopoverNext>;
}

function ComparePicker() {
  const { t } = useText();
  const { frame, dispatch } = useStatistics();
  const on = frame.comparison !== "none";
  return <PopoverNext placement="bottom-start" content={<Menu>
    {comparisons.map(comparison => <MenuItem key={comparison} text={comparisonLabel(t, comparison)} roleStructure="listoption" selected={frame.comparison === comparison}
      labelElement={frame.comparison === comparison ? <AppIcon name="check" size={14} /> : undefined} onClick={() => dispatch({ type: "comparison", comparison })} />)}
  </Menu>}>
    <Button size="small" className="stats-control" active={on} icon={<AppIcon name="history" size={14} />} endIcon={<AppIcon name="chevronDown" size={13} />}
      aria-label={t("Compare: {value}", { value: comparisonLabel(t, frame.comparison) })} title={t("Compare")}>{on ? comparisonLabel(t, frame.comparison) : t("Compare")}</Button>
  </PopoverNext>;
}

/** The menu "+ Filter": the kinds not set yet, then the values of the kind chosen. */
function AddFilter() {
  const { t } = useText();
  const { frame, dispatch, api, request, context, providerName } = useStatistics();
  const [open, setOpen] = useState(false);
  const [kind, setKind] = useState<FilterKey | null>(null);
  const [search, setSearch] = useState("");
  const wide = request({ period: "all", frequency: "month", comparison: "none", filter: {}, limit: 500 });
  const models = useStatisticsQuery(queryKey("models", wide), signal => api.models(wide, signal), open && (kind === "model" || kind === "provider" || kind === "effort"));
  const projects = useStatisticsQuery(queryKey("projects", wide), signal => api.projects(wide, signal), open && kind === "project");
  const choices = useMemo(() => kind ? filterChoices(kind, { models: models.data ?? null, projects: projects.data ?? null, spaces: context.spaces ?? [], word: (key, value) => filterValueLabel(t, key, value), provider: providerName }) : [],
    [kind, models.data, projects.data, context.spaces, providerName]); // eslint-disable-line react-hooks/exhaustive-deps
  const shown = search ? choices.filter(choice => `${choice.label} ${choice.detail ?? ""}`.toLowerCase().includes(search.toLowerCase())) : choices;
  const unset = unsetFilters(frame.filters);
  const loading = (kind === "project" && projects.loading) || ((kind === "model" || kind === "provider" || kind === "effort") && models.loading);
  const close = () => { setOpen(false); setKind(null); setSearch(""); };
  const pick = (choice: FilterChoice) => { if (kind) dispatch({ type: "filter", key: kind, entry: { value: choice.value, label: choice.label } }); close(); };
  return <PopoverNext placement="bottom-start" isOpen={open} onInteraction={next => { if (next) setOpen(true); else close(); }} content={<div className="stats-popover">
    {kind === null ? <Menu>{unset.length === 0 ? <MenuItem disabled text={t("Every filter is set")} /> : unset.map(key =>
      <MenuItem key={key} text={filterKindLabel(t, key)} shouldDismissPopover={false} labelElement={<AppIcon name="chevronRight" size={13} />} onClick={() => setKind(key)} />)}</Menu>
      : <div className="stats-filter-values">
        <div className="stats-filter-head"><Button variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={14} />} onClick={() => { setKind(null); setSearch(""); }}>{filterKindLabel(t, kind)}</Button></div>
        {choices.length > 8 && <input type="search" className="stats-filter-search" value={search} placeholder={t("Search")} aria-label={t("Search")} onChange={event => setSearch(event.target.value)} />}
        <Menu className="stats-filter-list">
          {loading && <MenuItem disabled text={t("Reading…")} />}
          {!loading && shown.length === 0 && <MenuItem disabled text={t("Nothing to choose")} />}
          {shown.map(choice => <MenuItem key={choice.value} text={choice.label} label={choice.detail} onClick={() => pick(choice)} />)}
        </Menu>
      </div>}
  </div>}>
    <Button size="small" variant="minimal" className="stats-add-filter" icon={<AppIcon name="plus" size={13} />} disabled={unset.length === 0}>{t("Filter")}</Button>
  </PopoverNext>;
}

/** The menu of the canvas: more history, forget the deleted, reset (which asks first). */
function CanvasMenu() {
  const { t } = useText();
  const { status, history } = useStatistics();
  const [confirming, setConfirming] = useState(false);
  const more = readMoreChoices(status);
  const label = (choice: HistoryChoice) => choice.kind === "all" ? t("All the history") : choice.kind === "days" ? t("Last {days} days", { days: choice.days }) : t("From today");
  const menu = <PopoverNext placement="bottom-end" content={<Menu>
    <MenuDivider title={t("Read more history…")} />
    {more.length === 0 ? <MenuItem disabled text={status?.state === "needsChoice" ? t("Choose how much to read first") : t("All the history is read")} />
      : more.map(choice => <MenuItem key={`${choice.kind}${choice.kind === "days" ? choice.days : ""}`} text={label(choice)} disabled={history.busy || status?.state === "reading"} onClick={() => void history.choose(choice)} />)}
    <MenuDivider />
    <MenuItem icon={<AppIcon name="trash" size={14} />} text={t("Forget deleted sessions")} disabled={history.busy} onClick={() => void history.forgetDeleted()} />
    {history.reset && <MenuItem icon={<AppIcon name="reset" size={14} />} text={t("Reset statistics…")} disabled={history.busy} onClick={() => setConfirming(true)} />}
  </Menu>}>
    <Button size="small" variant="minimal" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("Statistics menu")} title={t("Statistics menu")} />
  </PopoverNext>;
  return <PopoverNext placement="bottom-end" isOpen={confirming} onInteraction={next => { if (!next && !history.busy) setConfirming(false); }} content={
    <div className="stats-confirm" role="alertdialog" aria-label={t("Reset the statistics?")}>
      <strong>{t("Reset the statistics?")}</strong>
      <p>{t("All the numbers are deleted. You choose again how much history to read.")}</p>
      <div className="stats-confirm-actions">
        <Button variant="minimal" size="small" disabled={history.busy} onClick={() => setConfirming(false)}>{t("Cancel")}</Button>
        <Button intent="danger" size="small" autoFocus loading={history.busy} onClick={() => { void history.reset!().finally(() => setConfirming(false)); }}>{t("Reset")}</Button>
      </div>
    </div>}>{menu}</PopoverNext>;
}

/** The bar. */
export function FrameBar() {
  const { t } = useText();
  const { frame, dispatch, resetFrame, header, openFrame } = useStatistics();
  const ignored = ignoredKinds(header?.ignoredFilters ?? []);
  const set = filterKeys.filter(key => frame.filters[key]);
  const changed = encodeFrame({ ...frame, page: openFrame.page, view: openFrame.view }) !== encodeFrame(openFrame);
  return <div className="stats-frame" role="toolbar" aria-label={t("Statistics controls")}>
    <PeriodPicker />
    <FrequencyPicker />
    <ComparePicker />
    <span className="stats-frame-sep" aria-hidden="true" />
    <div className="stats-chips" role="group" aria-label={t("Filters")}>
      {set.map(key => {
        const entry = frame.filters[key]!;
        const text = `${filterKindLabel(t, key)}: ${filterValueLabel(t, key, entry.label ?? entry.value)}`;
        const note = ignored.has(key) ? t("This filter does not apply to this page.") : undefined;
        return <span key={key} className="stats-chip" role="group" data-ignored={note ? "" : undefined} title={note} aria-label={note ? `${text} — ${note}` : text}>
          <span className="stats-chip-text">{text}</span>
          <button type="button" className="stats-chip-remove" aria-label={t("Remove filter: {name}", { name: text })} onClick={() => dispatch({ type: "filter", key, entry: null })}><AppIcon name="close" size={12} /></button>
        </span>;
      })}
      <AddFilter />
    </div>
    <span className="stats-frame-spacer" />
    {changed && <Button size="small" variant="minimal" icon={<AppIcon name="reset" size={14} />} onClick={resetFrame}>{t("Reset")}</Button>}
    <CanvasMenu />
  </div>;
}
