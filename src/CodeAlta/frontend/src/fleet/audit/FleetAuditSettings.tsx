import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, ButtonGroup, Callout, Card, NonIdealState, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetAccessOverview, type FleetAuditCheck, type FleetAuditItem, type FleetServerView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../../SettingsPage";
import { DataTable, type DataColumn } from "../console/DataTable";
import { FilterBar, type FilterFacet } from "../console/FilterBar";
import { FleetHelp } from "../console/FleetHelp";
import type { FacetValues, TableSort } from "../console/tableRows";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { serverProblem } from "../fleetServer";
import { actionLabels, auditCategories, dayName, dayOf, formatDetails, isFiltered, noFilter, shortHash, succeeded, targetNames, targetOf, toFilters, type AuditFilterState, type AuditOutcome,
  type AuditPeriod } from "./audit";

export type FleetAuditApi = Pick<typeof fleetServer, "status" | "access" | "devices" | "audit" | "verifyAudit" | "exportAudit" | "revealAuditExport">;

const call = { timeoutMilliseconds: 30_000 };
const pageSize = 100;
// Words are read as they are typed, a moment after the last key.
const typingMilliseconds = 350;
// The clock of the page; tests give their own, the same one at every render.
const systemNow = () => new Date();

/**
 * Settings > Fleet > Audit: the audit log of each server this computer is linked to, for owners, admins and auditors. A
 * table of its records, newest first under the day they were recorded, filtered by kind of action, person, result, period
 * and words; the check of the chain of hashes that shows nothing was changed, removed or inserted; and exports of what the
 * filters select, as CSV for a spreadsheet or JSON Lines whose hashes verify elsewhere.
 */
export function FleetAuditSettings({ epoch, api = fleetServer, now = systemNow }: { epoch: string | null; api?: FleetAuditApi; now?: () => Date }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-audit-settings" label={t("Audit")} group="Fleet" title="Audit"
    description="Who did what in your fleet, and how it ended." notice={notice}
    loading={loading} busy={busy} onReload={reload}
    actions={<FleetHelp text="Who did what in your fleet, with which device, from where, and how it ended. The log holds no content: no prompt, no answer, no file." page="fleet/policies-and-audit.md" />}>
    {!listing ? <SettingsUnavailable loading={loading} icon="logs" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-secrets-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")} />
      : servers.map(server => <ServerAudit key={server.id} server={server} epoch={epoch ?? ""} api={api} titled={servers.length > 1} now={now} />)}
  </SettingsPage>;
}

type Problem = { key: MessageKey; detail?: string | null };

/** The audit log of one server, for a person who may read it. */
function ServerAudit({ server, epoch, api, titled, now }: { server: FleetServerView; epoch: string; api: FleetAuditApi; titled: boolean; now: () => Date }) {
  const { t } = useShellLanguage();
  const [access, setAccess] = useState<FleetAccessOverview | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  // The devices of the organization, for the names of the records that reach one; the log reads without them.
  const [devices, setDevices] = useState<readonly { id: string; name: string }[]>([]);
  useEffect(() => {
    const abort = new AbortController();
    api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, ...call }).then(value => { if (!abort.signal.aborted && value.status === "ok") setDevices(value.devices); },
      () => { });
    return () => abort.abort();
  }, [api, epoch, server.id]);
  useEffect(() => {
    const abort = new AbortController();
    api.access({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, ...call }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setAccess(value); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id]);
  if (!access) return <section className="fleet-secrets-server" aria-label={server.url}>
    {problem ? <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>
      : <ActivitySpinner size={16} />}
  </section>;
  return <section className="fleet-secrets-server fleet-audit-server settings-grid" aria-label={server.url}>
    {titled && <h2 className="fleet-secrets-server-title">{server.organization ?? server.url}</h2>}
    {access.capabilities.includes("audit.read")
      ? <AuditLog server={server} epoch={epoch} api={api} access={access} now={now} names={targetNames(access.people, access.teams, devices)} />
      : <NonIdealState className="fleet-audit-closed" icon={<AppIcon name="logs" size={32} />} title={t("Owners, admins and auditors read the audit log.")} />}
  </section>;
}

const periodLabels: readonly { value: AuditPeriod; label: MessageKey }[] = Object.freeze([
  { value: "day", label: "Last 24 hours" }, { value: "week", label: "Last 7 days" }, { value: "month", label: "Last 30 days" }, { value: "custom", label: "Between two days…" },
]);

/** The facets of the filter bar as the filter holds them, and back: the server answers them, a page at a time. */
function facetValuesOf(filter: AuditFilterState): FacetValues {
  return { what: filter.categories, who: filter.person ? [filter.person] : [], result: filter.outcome === "any" ? [] : [filter.outcome],
    period: filter.period === "any" ? [] : [filter.period] };
}

/** The table of records, its filters, the check of its chain and its exports. */
function AuditLog({ server, epoch, api, access, now, names }: {
  server: FleetServerView; epoch: string; api: FleetAuditApi; access: FleetAccessOverview; now: () => Date; names: ReadonlyMap<string, string>;
}) {
  const { t, locale } = useShellLanguage();
  const linkId = server.id;
  const [filter, setFilter] = useState<AuditFilterState>(noFilter);
  const [text, setText] = useState("");
  const [items, setItems] = useState<FleetAuditItem[]>([]);
  const [before, setBefore] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [generation, setGeneration] = useState(0);
  const [check, setCheck] = useState<FleetAuditCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [exporting, setExporting] = useState<string | null>(null);
  const [exported, setExported] = useState<{ path: string; kilobytes: number } | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [sort, setSort] = useState<TableSort | null>({ column: "time", direction: "desc" });
  const filters = useMemo(() => toFilters(filter, now()), [filter, now, generation]);
  const canExport = access.capabilities.includes("audit.export");
  const loadAbort = useRef<AbortController | null>(null);

  // The words are taken a moment after the last key, so that each key does not ask the server; none at once.
  useEffect(() => {
    const timer = setTimeout(() => setFilter(value => value.text === text ? value : { ...value, text }), text ? typingMilliseconds : 0);
    return () => clearTimeout(timer);
  }, [text]);

  useEffect(() => {
    loadAbort.current?.abort();
    if (!filters) { setItems([]); setBefore(null); setLoading(false); setProblem({ key: "The days of the period do not read." }); return; }
    const abort = new AbortController();
    loadAbort.current = abort;
    setLoading(true);
    api.audit({ expectedEpoch: epoch, linkId, filters, before: null, limit: pageSize }, { signal: abort.signal, ...call }).then(page => {
      if (abort.signal.aborted) return;
      setLoading(false);
      if (page.status === "ok") { setItems([...page.items]); setBefore(page.before); setProblem(null); setOpen(null); }
      else setProblem({ key: serverProblem(page.status) ?? "The operation did not complete.", detail: page.message });
    }, () => { if (!abort.signal.aborted) { setLoading(false); setProblem({ key: "The server does not answer." }); } });
    return () => abort.abort();
  }, [api, epoch, linkId, filters]);

  // An older page belongs to the filters it was asked with: a change of filters aborts it, and a late answer is dropped.
  const older = useCallback(async () => {
    if (before === null || !filters) return;
    loadAbort.current?.abort();
    const abort = new AbortController();
    loadAbort.current = abort;
    setLoading(true);
    try {
      const page = await api.audit({ expectedEpoch: epoch, linkId, filters, before, limit: pageSize }, { signal: abort.signal, ...call });
      if (abort.signal.aborted) return;
      if (page.status === "ok") { setItems(value => [...value, ...page.items]); setBefore(page.before); }
      else setProblem({ key: serverProblem(page.status) ?? "The operation did not complete.", detail: page.message });
    } catch {
      if (!abort.signal.aborted) setProblem({ key: "The server does not answer." });
    } finally {
      if (!abort.signal.aborted) setLoading(false);
    }
  }, [api, epoch, linkId, filters, before]);

  const verify = async () => {
    setChecking(true);
    try {
      const value = await api.verifyAudit({ expectedEpoch: epoch, linkId }, { timeoutMilliseconds: 600_000 });
      if (value.status === "ok") setCheck(value);
      else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally {
      setChecking(false);
    }
  };

  const exportAs = async (format: "csv" | "jsonl") => {
    if (!filters) return;
    setExporting(format);
    setExported(null);
    try {
      const value = await api.exportAudit({ expectedEpoch: epoch, linkId, filters, format }, { timeoutMilliseconds: 600_000 });
      if (value.status === "ok" && value.path) { setExported({ path: value.path, kilobytes: value.kilobytes }); setProblem(null); }
      else if (value.status !== "canceled") setProblem({ key: value.status === "unavailable" ? "This computer cannot show a dialog to save the file." : serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally {
      setExporting(null);
    }
  };

  const set = (change: Partial<AuditFilterState>) => setFilter(value => ({ ...value, ...change }));
  const timeFormat = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  const dayFormat = new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  const checkedFormat = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  const facets: FilterFacet[] = [
    { id: "what", label: "What", icon: "filter", options: auditCategories.map(category => ({ value: category.id, label: t(category.label) })) },
    { id: "who", label: "Who", icon: "user", single: true, options: access.people.map(person => ({ value: person.id, label: person.name })) },
    { id: "result", label: "Result", icon: "issueOpen", single: true, options: [{ value: "success", label: t("Succeeded") }, { value: "failure", label: t("Refused or failed") }] },
    { id: "period", label: "Period", icon: "calendar", single: true, options: periodLabels.map(period => ({ value: period.value, label: t(period.label) })) },
  ];
  const onValues = (values: FacetValues) => set({ categories: values.what ?? [], person: values.who?.[0] ?? "", outcome: (values.result?.[0] ?? "any") as AuditOutcome,
    period: (values.period?.[0] ?? "any") as AuditPeriod });
  // How many records each day has among those read, for the line that names it.
  const perDay = useMemo(() => {
    const counts = new Map<string, number>();
    for (const item of items) { const key = dayOf(item.recordedAt).key; counts.set(key, (counts.get(key) ?? 0) + 1); }
    return counts;
  }, [items]);
  const targetText = (item: FleetAuditItem) => {
    const target = targetOf(item, names);
    return !target ? null : "name" in target ? target.name : t("Version {version}", { version: target.version });
  };
  const columns: DataColumn<FleetAuditItem>[] = [
    { id: "time", label: "Time", width: "6.5rem", sort: item => item.sequence, cell: item => <span className="fleet-audit-time" title={new Date(item.recordedAt).toLocaleString(locale)}>
      {timeFormat.format(new Date(item.recordedAt))}</span> },
    { id: "what", label: "Action", sort: item => actionLabels[item.action] ? t(actionLabels[item.action]) : item.action, cell: item => {
      const label = actionLabels[item.action];
      const who = item.personName ?? item.personId;
      const device = item.deviceName ?? item.deviceId;
      return <span className="fleet-audit-what">
        <span className="fleet-audit-label" title={item.action}>{label ? t(label) : item.action}</span>
        {label && <code className="fleet-audit-code">{item.action}</code>}
        {/* A narrow page has no column of who did it: it says it under the action. */}
        <span className="fleet-audit-what-who muted-text">{who ?? t("The server")}{device && ` · ${device}`}</span>
      </span>;
    } },
    { id: "who", label: "Who", sort: item => item.personName ?? item.personId ?? null, cell: item => {
      const who = item.personName ?? item.personId;
      const device = item.deviceName ?? item.deviceId;
      return <span className="fleet-audit-who">
        <AppIcon name={who ? "user" : "server"} size={13} />
        <span className="fleet-audit-who-name">{who ?? t("The server")}{device && <span className="muted-text"> · {device}</span>}</span>
      </span>;
    } },
    { id: "result", label: "Result", width: "10rem", sort: item => succeeded(item) ? "" : item.result, cell: item => succeeded(item)
      ? <span className="fleet-audit-ok"><AppIcon name="check" size={13} />{t("Succeeded")}</span>
      : <Tag minimal intent="danger" className="fleet-audit-result" title={item.result}>{t("Refused: {code}", { code: item.result })}</Tag> },
    { id: "target", label: "Target", secondary: true, sort: item => targetText(item) ?? item.target, cell: item => {
      if (!item.target) return <span className="muted-text">—</span>;
      const said = targetText(item);
      return said ? <span className="fleet-audit-target-name" title={item.target}>{said}</span> : <code className="fleet-audit-target" title={item.target}>{item.target}</code>;
    } },
  ];
  const status = loading && items.length > 0 ? <ActivitySpinner size={12} />
    : t(before !== null ? "The newest {count} records" : "{count} records", { count: items.length });
  return <Card className="appearance-settings fleet-audit-card" aria-labelledby={`audit-${linkId}`}>
    <div className="fleet-secrets-head fleet-audit-head">
      <h2 id={`audit-${linkId}`}>{t("Audit log")}</h2>
      <span className="fleet-audit-actions">
        <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Read the log again")} title={t("Read the log again")} disabled={loading}
          onClick={() => setGeneration(value => value + 1)} />
        <Button size="small" icon={<AppIcon name="shield" size={14} />} text={t("Verify the chain")} loading={checking} onClick={() => void verify()} />
        {canExport && <ButtonGroup size="small" aria-label={t("Export")}>
          <Button size="small" icon={<AppIcon name="download" size={14} />} text={t("Export CSV…")} loading={exporting === "csv"} disabled={exporting !== null || !filters}
            onClick={() => void exportAs("csv")} />
          <Button size="small" text={t("Export JSON Lines…")} loading={exporting === "jsonl"} disabled={exporting !== null || !filters} onClick={() => void exportAs("jsonl")} />
        </ButtonGroup>}
      </span>
    </div>
    <Verification check={check} format={checkedFormat} />
    {exported && <Callout compact intent="success" role="status" className="fleet-audit-exported">
      <span className="fleet-audit-exported-text">{t("Exported to {path} ({size} KB).", { path: exported.path, size: exported.kilobytes })}</span>
      <Button size="small" variant="minimal" text={t("Show in folder")} onClick={() => void api.revealAuditExport({ expectedEpoch: epoch }, call)} />
    </Callout>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    <section className="fleet-table-section fleet-audit-table" aria-label={t("Audit log")}>
      <FilterBar label={t("Filters of the audit log")} query={text} onQuery={setText} placeholder="Search the log" facets={facets} values={facetValuesOf(filter)} onValues={onValues}
        status={status}>
        {filter.period === "custom" && <span className="fleet-audit-days">
          <input className="bp6-input bp6-small" type="date" aria-label={t("From")} value={filter.from} onChange={event => set({ from: event.currentTarget.value })} />
          <span aria-hidden="true">–</span>
          <input className="bp6-input bp6-small" type="date" aria-label={t("To")} value={filter.to} onChange={event => set({ to: event.currentTarget.value })} />
        </span>}
      </FilterBar>
      {items.length === 0 && loading ? <div className="fleet-audit-loading"><ActivitySpinner size={16} /></div>
        : <DataTable label={t("Audit log")} rows={items} columns={columns} rowKey={item => String(item.sequence)} sort={sort} onSort={setSort} selected={open} onSelect={setOpen}
          pageSize={Math.max(items.length, 1)} rowState={item => succeeded(item) ? undefined : "refused"}
          rowProps={item => ({ className: "fleet-audit-row", data: { sequence: String(item.sequence), action: item.action } })}
          group={sort?.column === "time" ? item => {
            const day = dayOf(item.recordedAt);
            const name = dayName(day.date, now());
            return { key: day.key, label: <span className="fleet-audit-day-title">{name === "today" ? t("Today") : name === "yesterday" ? t("Yesterday") : dayFormat.format(day.date)}
              <span className="muted-text">{perDay.get(day.key)}</span></span> };
          } : null}
          empty={<span className="muted-text fleet-audit-empty">{t(isFiltered(filter) ? "No record matches these filters." : "The log has no record yet.")}</span>}
          details={item => <Details item={item} target={targetText(item)} />} />}
      {before !== null && items.length > 0 && <Button className="fleet-audit-older" size="small" variant="minimal" icon={<AppIcon name="chevronDown" size={14} />}
        text={t("Show older records")} loading={loading} onClick={() => void older()} />}
    </section>
  </Card>;
}

/** What the last check of the chain said. */
function Verification({ check, format }: { check: FleetAuditCheck | null; format: Intl.DateTimeFormat }) {
  const { t } = useShellLanguage();
  if (!check) return <p className="muted-text fleet-audit-hint">{t("Each record holds the hash of the one before it: Verify the chain checks that none was changed, removed or inserted.")}</p>;
  const at = check.checkedAt ? format.format(new Date(check.checkedAt)) : "";
  return check.valid
    ? <Callout compact intent="success" role="status" className="fleet-audit-check">
      {t("The chain holds: {count} records, checked {time}.", { count: check.count, time: at })}
      {check.headHash && <span className="muted-text fleet-audit-head-hash"> {t("Last hash")} <code>{shortHash(check.headHash)}</code></span>}
    </Callout>
    : <Callout compact intent="danger" role="alert" className="fleet-audit-check">
      {t("The chain breaks at record {number}: a record was changed, removed or inserted. Export the log and tell the owner of the server.", { number: check.brokenAt ?? 0 })}
    </Callout>;
}

/** The details of a record, under its row: where it came from, what it reached, its place in the chain and what it said. */
function Details({ item, target }: { item: FleetAuditItem; target: string | null }) {
  const { t, locale } = useShellLanguage();
  const details = formatDetails(item.details);
  return <div className="fleet-audit-details">
    <dl>
      <div><dt>{t("When")}</dt><dd>{new Date(item.recordedAt).toLocaleString(locale, { dateStyle: "full", timeStyle: "medium" })}</dd></div>
      <div><dt>{t("Action")}</dt><dd><code>{item.action}</code></dd></div>
      {(item.deviceName || item.deviceId) && <div><dt>{t("Device")}</dt><dd>{item.deviceName ?? item.deviceId}</dd></div>}
      {item.target && <div><dt>{t("Target")}</dt><dd>{target && <>{target} </>}<code className="muted-text">{item.target}</code></dd></div>}
      {item.address && <div><dt>{t("Address")}</dt><dd><code>{item.address}</code></dd></div>}
      <div><dt>{t("Record")}</dt><dd>#{item.sequence}{item.requestId && <span className="muted-text"> · {t("request")} <code>{item.requestId}</code></span>}</dd></div>
      <div><dt>{t("Hash")}</dt><dd><code title={item.hash}>{shortHash(item.hash)}</code><span className="muted-text"> ← <code title={item.previousHash}>{item.previousHash ? shortHash(item.previousHash) : "—"}</code></span></dd></div>
    </dl>
    {details && <pre className="fleet-audit-json" aria-label={t("Details")}>{details}</pre>}
  </div>;
}
