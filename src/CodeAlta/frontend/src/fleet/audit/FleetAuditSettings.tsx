import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, ButtonGroup, Callout, Card, HTMLSelect, InputGroup, NonIdealState, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetAccessOverview, type FleetAuditCheck, type FleetAuditItem, type FleetServerView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../../SettingsPage";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { serverProblem } from "../fleetServer";
import { actionLabels, auditCategories, dayName, formatDetails, groupByDay, isFiltered, noFilter, shortHash, succeeded, toFilters, type AuditFilterState, type AuditOutcome,
  type AuditPeriod } from "./audit";

export type FleetAuditApi = Pick<typeof fleetServer, "status" | "access" | "audit" | "verifyAudit" | "exportAudit" | "revealAuditExport">;

const call = { timeoutMilliseconds: 30_000 };
const pageSize = 100;
// Words are read as they are typed, a moment after the last key.
const typingMilliseconds = 350;
// The clock of the page; tests give their own, the same one at every render.
const systemNow = () => new Date();

/**
 * Settings > Fleet > Audit: the audit log of each server this computer is linked to, for owners, admins and auditors. A
 * timeline by day, newest first, filtered by kind of action, person, result, period and words; the check of the chain of
 * hashes that shows nothing was changed, removed or inserted; and exports of what the filters select, as CSV for a
 * spreadsheet or JSON Lines whose hashes verify elsewhere.
 */
export function FleetAuditSettings({ epoch, api = fleetServer, now = systemNow }: { epoch: string | null; api?: FleetAuditApi; now?: () => Date }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-audit-settings" label={t("Audit")} group="Fleet" title="Audit"
    description="Who did what in your fleet, with which device, from where, and how it ended. The log holds no content: no prompt, no answer, no file." notice={notice}
    loading={loading} busy={busy} onReload={reload}>
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
      ? <AuditLog server={server} epoch={epoch} api={api} access={access} now={now} />
      : <NonIdealState className="fleet-audit-closed" icon={<AppIcon name="logs" size={32} />} title={t("Owners, admins and auditors read the audit log.")} />}
  </section>;
}

/** The timeline, its filters, the check of its chain and its exports. */
function AuditLog({ server, epoch, api, access, now }: { server: FleetServerView; epoch: string; api: FleetAuditApi; access: FleetAccessOverview; now: () => Date }) {
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
  const [open, setOpen] = useState<number | null>(null);
  const filters = useMemo(() => toFilters(filter, now()), [filter, now, generation]);
  const canExport = access.capabilities.includes("audit.export");
  const loadAbort = useRef<AbortController | null>(null);

  // The words are taken a moment after the last key, so that each key does not ask the server.
  useEffect(() => {
    const timer = setTimeout(() => setFilter(value => value.text === text ? value : { ...value, text }), typingMilliseconds);
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
  const days = groupByDay(items);
  const timeFormat = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  const dayFormat = new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  const checkedFormat = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
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
    <div className="fleet-audit-filters" role="search" aria-label={t("Filters of the audit log")}>
      <InputGroup className="fleet-audit-search" size="small" type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Search the log")} aria-label={t("Search the log")}
        value={text} onValueChange={value => setText(value)} />
      <HTMLSelect aria-label={t("What")} value={filter.category} onChange={event => set({ category: event.currentTarget.value })}
        options={auditCategories.map(category => ({ value: category.id, label: t(category.label) }))} />
      <HTMLSelect aria-label={t("Who")} value={filter.person} onChange={event => set({ person: event.currentTarget.value })}
        options={[{ value: "", label: t("Anyone") }, ...access.people.map(person => ({ value: person.id, label: person.name }))]} />
      <HTMLSelect aria-label={t("Result")} value={filter.outcome} onChange={event => set({ outcome: event.currentTarget.value as AuditOutcome })}
        options={[{ value: "any", label: t("Any result") }, { value: "success", label: t("Succeeded") }, { value: "failure", label: t("Refused or failed") }]} />
      <HTMLSelect aria-label={t("Period")} value={filter.period} onChange={event => set({ period: event.currentTarget.value as AuditPeriod })}
        options={[{ value: "any", label: t("Any time") }, { value: "day", label: t("Last 24 hours") }, { value: "week", label: t("Last 7 days") },
          { value: "month", label: t("Last 30 days") }, { value: "custom", label: t("Between two days…") }]} />
      {filter.period === "custom" && <span className="fleet-audit-days">
        <input className="bp6-input bp6-small" type="date" aria-label={t("From")} value={filter.from} onChange={event => set({ from: event.currentTarget.value })} />
        <span aria-hidden="true">–</span>
        <input className="bp6-input bp6-small" type="date" aria-label={t("To")} value={filter.to} onChange={event => set({ to: event.currentTarget.value })} />
      </span>}
      {isFiltered(filter) && <Button size="small" variant="minimal" text={t("Clear the filters")} onClick={() => { setText(""); setFilter(noFilter); }} />}
    </div>
    {items.length === 0
      ? loading ? <ActivitySpinner size={16} /> : <p className="muted-text fleet-audit-empty">{t(isFiltered(filter) ? "No record matches these filters." : "The log has no record yet.")}</p>
      : <ol className="fleet-audit-days-list" aria-label={t("Audit log")}>
        {days.map(group => {
          const name = dayName(group.date, now());
          return <li key={group.key} className="fleet-audit-day">
            <h3 className="fleet-audit-day-title">{name === "today" ? t("Today") : name === "yesterday" ? t("Yesterday") : dayFormat.format(group.date)}</h3>
            <ul className="fleet-audit-list">
              {group.items.map(item => <Row key={item.sequence} item={item} time={timeFormat.format(new Date(item.recordedAt))} expanded={open === item.sequence}
                onToggle={() => setOpen(value => value === item.sequence ? null : item.sequence)} />)}
            </ul>
          </li>;
        })}
      </ol>}
    {before !== null && items.length > 0 && <Button className="fleet-audit-older" size="small" variant="minimal" icon={<AppIcon name="chevronDown" size={14} />}
      text={t("Show older records")} loading={loading} onClick={() => void older()} />}
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

/** One record: when, what, who, how it ended; its details below once opened. */
function Row({ item, time, expanded, onToggle }: { item: FleetAuditItem; time: string; expanded: boolean; onToggle: () => void }) {
  const { t } = useShellLanguage();
  const ok = succeeded(item);
  const label = actionLabels[item.action];
  const who = item.personName ?? item.personId;
  const device = item.deviceName ?? item.deviceId;
  const details = expanded ? formatDetails(item.details) : null;
  return <li className={`fleet-audit-row${ok ? "" : " is-refused"}`} data-sequence={item.sequence} data-action={item.action}>
    <button type="button" className="fleet-audit-summary" aria-expanded={expanded} onClick={onToggle}>
      <span className="fleet-audit-time">{time}</span>
      <span className={`fleet-audit-mark ${ok ? "is-ok" : "is-refused"}`} aria-hidden="true" />
      <span className="fleet-audit-what">
        <span className="fleet-audit-label">{label ? t(label) : item.action}</span>
        {!ok && <Tag minimal intent="danger" className="fleet-audit-result">{t("Refused: {code}", { code: item.result })}</Tag>}
        {label && <code className="fleet-audit-code">{item.action}</code>}
      </span>
      <span className="fleet-audit-who">{who ?? t("The server")}{device && <span className="muted-text"> · {device}</span>}</span>
    </button>
    {expanded && <dl className="fleet-audit-details">
      {item.target && <><dt>{t("Target")}</dt><dd><code>{item.target}</code></dd></>}
      {item.address && <><dt>{t("Address")}</dt><dd><code>{item.address}</code></dd></>}
      <dt>{t("Record")}</dt><dd>#{item.sequence}{item.requestId && <span className="muted-text"> · {t("request")} <code>{item.requestId}</code></span>}</dd>
      <dt>{t("Hash")}</dt><dd><code title={item.hash}>{shortHash(item.hash)}</code><span className="muted-text"> ← <code title={item.previousHash}>{item.previousHash ? shortHash(item.previousHash) : "—"}</code></span></dd>
      {details && <><dt>{t("Details")}</dt><dd><pre className="fleet-audit-json">{details}</pre></dd></>}
    </dl>}
  </li>;
}
