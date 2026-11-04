import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Card, HTMLSelect, InputGroup, NonIdealState, PopoverNext, ProgressBar, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetBudgetHoldView, type FleetBudgetsOverview, type FleetBudgetView, type FleetNotificationView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { ConfirmPopover } from "../ConfirmPopover";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { serverProblem } from "./fleetServer";
import { budgetIntent, budgetScopeLabel, budgetScopeOptions, formatAmount, newNotifications, notificationIntent, notificationPhrase, parseLimit, usedShare,
  waitingHolds } from "./budgets";

export type FleetBudgetsApi = Pick<typeof fleetServer, "status" | "budgets" | "setBudget" | "removeBudget" | "continueRun" | "stopRun" | "readNotification">;
type Result = Promise<{ status: string; message?: string | null }>;

/** What a budget limits, as the form sets it. */
export type BudgetDraft = { name: string; scope: string; measure: string; limit: string; period: string; mode: string; reached: string };

/** How often the budgets of a server are read again while the page shows them, so that consumption moves and a run that waits appears. */
const pollMilliseconds = 10_000;

const periodLabels: Record<string, MessageKey> = { day: "Per day", week: "Per week", month: "Per month" };

/**
 * Settings > Fleet > Budgets: the budgets of the person at each server this computer manages, each with its consumption in its
 * period; Add, Edit and Remove beside each row; the runs that wait for a reached budget, continued anyway or stopped beside their
 * row; and the notifications not dismissed yet.
 */
export function FleetBudgetsSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetBudgetsApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-budgets-settings" label={t("Budgets")} group="Fleet" title="Budgets"
    description="Limits of tokens or estimated cost for your machines, per day, week or month." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="usage" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-secrets-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")} />
      : servers.map(server => <ServerBudgets key={server.id} server={server} epoch={epoch ?? ""} api={api} titled={servers.length > 1} />)}
  </SettingsPage>;
}

/** The budgets, the runs that wait and the notifications of one server. */
function ServerBudgets({ server, epoch, api, titled }: { server: FleetServerView; epoch: string; api: FleetBudgetsApi; titled: boolean }) {
  const { t } = useShellLanguage();
  const [overview, setOverview] = useState<FleetBudgetsOverview | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [adding, setAdding] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    api.budgets({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setOverview(value); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id, generation]);
  useEffect(() => {
    const timer = setInterval(() => setGeneration(value => value + 1), pollMilliseconds);
    return () => clearInterval(timer);
  }, []);
  const run = useCallback(async (call: () => Result) => {
    setBusy(true);
    try {
      const result = await call();
      if (result.status === "ok") { setProblem(null); return true; }
      setProblem({ key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
      return false;
    } catch {
      setProblem({ key: "The server does not answer." });
      return false;
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  }, []);
  const linkId = server.id;
  if (!overview) return <section className="fleet-secrets-server" aria-label={server.url}>
    {problem ? <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>
      : <ActivitySpinner size={16} />}
  </section>;
  const waiting = waitingHolds(overview);
  const fresh = newNotifications(overview);
  const save = (id: string | null, draft: BudgetDraft) => {
    const scope = budgetScopeOptions(overview).find(option => option.value === draft.scope) ?? budgetScopeOptions(overview)[0];
    return run(() => api.setBudget({ expectedEpoch: epoch, linkId, id, name: draft.name.trim(), scopeKind: scope.kind, scopeId: scope.id, measure: draft.measure,
      limit: parseLimit(draft.limit, draft.measure) ?? 0, period: draft.period, mode: draft.mode, reached: draft.reached }, { timeoutMilliseconds: 30_000 }));
  };
  return <section className="fleet-secrets-server settings-grid" aria-label={server.url}>
    {titled && <h2 className="fleet-secrets-server-title">{server.organization ?? server.url}</h2>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {waiting.length > 0 && <Card className="appearance-settings fleet-secrets-approvals" aria-labelledby={`holds-${linkId}`}>
      <h2 id={`holds-${linkId}`}>{t("Runs that wait for a budget")}</h2>
      <ul className="secret-list" aria-label={t("Runs that wait for a budget")}>
        {waiting.map(hold => <HoldRow key={hold.id} hold={hold} busy={busy}
          onContinue={() => void run(() => api.continueRun({ expectedEpoch: epoch, linkId, id: hold.id }, { timeoutMilliseconds: 30_000 }))}
          onStop={() => void run(() => api.stopRun({ expectedEpoch: epoch, linkId, id: hold.id }, { timeoutMilliseconds: 30_000 }))} />)}
      </ul>
    </Card>}
    <Card className="appearance-settings" aria-labelledby={`budgets-${linkId}`}>
      <div className="fleet-secrets-head">
        <h2 id={`budgets-${linkId}`}>{t("Budgets")}</h2>
        <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} disabled={busy || adding} onClick={() => setAdding(true)}>{t("Add a budget…")}</Button>
      </div>
      {adding && <BudgetForm overview={overview} busy={busy} onCancel={() => setAdding(false)} onSave={async draft => { if (await save(null, draft)) setAdding(false); }} />}
      {overview.budgets.length === 0 && !adding
        ? <NonIdealState className="secrets-empty" icon={<AppIcon name="usage" size={28} />} title={t("No budget yet.")} />
        : <ul className="secret-list" aria-label={t("Budgets")}>
          {overview.budgets.map(budget => <BudgetRow key={budget.id} budget={budget} overview={overview} busy={busy} save={draft => save(budget.id, draft)}
            remove={() => run(() => api.removeBudget({ expectedEpoch: epoch, linkId, id: budget.id }, { timeoutMilliseconds: 30_000 }))} />)}
        </ul>}
    </Card>
    {fresh.length > 0 && <Card className="appearance-settings" aria-labelledby={`notifications-${linkId}`}>
      <h2 id={`notifications-${linkId}`}>{t("Notifications")}</h2>
      <ul className="secret-list" aria-label={t("Notifications")}>
        {fresh.map(notification => <NotificationRow key={notification.id} notification={notification} busy={busy}
          onDismiss={() => void run(() => api.readNotification({ expectedEpoch: epoch, linkId, id: notification.id }, { timeoutMilliseconds: 30_000 }))} />)}
      </ul>
    </Card>}
  </section>;
}

/** One budget: its name and scope, its period and rules, its consumption bar; Edit and Remove. */
function BudgetRow({ budget, overview, busy, save, remove }: {
  budget: FleetBudgetView; overview: FleetBudgetsOverview; busy: boolean; save: (draft: BudgetDraft) => Promise<boolean>; remove: () => Promise<boolean>;
}) {
  const { t, locale } = useShellLanguage();
  const [editing, setEditing] = useState(false);
  const [removing, setRemoving] = useState(false);
  const scope = budgetScopeLabel(budget);
  const share = usedShare(budget);
  const until = budget.periodEnd ? new Date(budget.periodEnd).toLocaleDateString(locale, { dateStyle: "medium" }) : null;
  return <li className="secret-row fleet-budget-row" data-budget={budget.name} data-state={budget.state}>
    <span className="secret-name"><code title={budget.name}>{budget.name}</code>
      <span className="secret-reference">{t(scope.key, { name: scope.name ?? "" })}</span></span>
    <span className="fleet-secret-tags">
      <Tag minimal>{t(periodLabels[budget.period] ?? "Per month")}</Tag>
      <Tag minimal intent={budget.mode === "enforced" ? "primary" : "none"}>{t(budget.mode === "enforced" ? "Enforced" : "Advisory")}</Tag>
      <Tag minimal intent={budget.reached === "stop" ? "danger" : "warning"}>{t(budget.reached === "stop" ? "Stops new runs" : "Holds new runs")}</Tag>
    </span>
    <span className="fleet-budget-usage">
      <ProgressBar value={share} intent={budgetIntent(budget.state)} stripes={false} animate={false} aria-label={budget.name} />
      <span className="secret-meta">{t("{used} of {limit}", { used: formatAmount(budget.measure, budget.used, locale), limit: formatAmount(budget.measure, budget.limit, locale) })}
        {budget.measure === "tokens" ? ` ${t("Tokens").toLowerCase()}` : ""} · {Math.round(share * 100)}%{until ? ` · ${t("Until {date}", { date: until })}` : ""}</span>
    </span>
    <span className="secret-actions">
      <PopoverNext isOpen={editing} placement="bottom-end" onInteraction={next => { if (!next && !busy) setEditing(false); }}
        content={<BudgetForm overview={overview} budget={budget} busy={busy} onCancel={() => setEditing(false)}
          onSave={async draft => { if (await save(draft)) setEditing(false); }} />}>
        <Button size="small" icon={<AppIcon name="edit" size={14} />} text={t("Edit")} aria-label={t("Edit {name}", { name: budget.name })} disabled={busy}
          onClick={() => setEditing(true)} />
      </PopoverNext>
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove {name}", { name: budget.name })}
          disabled={busy} onClick={() => setRemoving(true)} />
        {removing && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Remove this budget?")} subject={budget.name}
          detail={t("Its limit no longer applies.")} confirmLabel={t("Remove")} onConfirm={() => { setRemoving(false); void remove(); }} onCancel={() => setRemoving(false)} />}
      </span>
    </span>
  </li>;
}

/** The form of a budget: a new one inline, an existing one in its popover. */
function BudgetForm({ overview, budget, busy, onSave, onCancel }: {
  overview: FleetBudgetsOverview; budget?: FleetBudgetView; busy: boolean; onSave: (draft: BudgetDraft) => Promise<void>; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const scopes = budgetScopeOptions(overview);
  const initialScope = budget ? scopes.find(option => option.kind === budget.scopeKind && (option.kind === "person" || option.id === budget.scopeId || option.name === budget.scopeLabel))
    : scopes[0];
  const [draft, setDraft] = useState<BudgetDraft>(() => ({
    name: budget?.name ?? "", scope: initialScope?.value ?? "person", measure: budget?.measure ?? "tokens", limit: budget ? String(budget.limit) : "",
    period: budget?.period ?? "month", mode: budget?.mode ?? "enforced", reached: budget?.reached ?? "hold",
  }));
  const change = (field: keyof BudgetDraft) => (event: { target: { value: string } }) => setDraft(current => ({ ...current, [field]: event.target.value }));
  const name = draft.name.trim();
  const limit = parseLimit(draft.limit, draft.measure);
  const ready = name.length > 0 && name.length <= 64 && limit !== null && !busy;
  return <form className="fleet-secret-new fleet-budget-form" aria-label={budget ? t("Edit {name}", { name: budget.name }) : t("Add a budget…")}
    onSubmit={event => { event.preventDefault(); if (ready) void onSave(draft); }}>
    <div className="fleet-secret-new-row">
      <InputGroup small autoFocus value={draft.name} placeholder={t("Name")} disabled={busy} aria-label={t("Name")} onChange={change("name")} />
      <HTMLSelect value={draft.scope} disabled={busy || !!budget} aria-label={t("Where it applies")} onChange={change("scope")}
        options={scopes.map(option => ({ value: option.value, label: t(option.label, { name: option.name ?? "" }) }))} />
    </div>
    <div className="fleet-secret-new-row">
      <HTMLSelect value={draft.measure} disabled={busy} aria-label={t("What it limits")} onChange={change("measure")}
        options={[{ value: "tokens", label: t("Tokens") }, { value: "cost", label: t("Estimated cost (USD)") }]} />
      <InputGroup small value={draft.limit} inputMode="decimal" placeholder={draft.measure === "cost" ? "50" : "2000000"} disabled={busy}
        intent={draft.limit && limit === null ? "danger" : "none"} aria-label={t("Limit")} onChange={change("limit")} />
      <HTMLSelect value={draft.period} disabled={busy} aria-label={t("Period")} onChange={change("period")}
        options={[{ value: "day", label: t("Per day") }, { value: "week", label: t("Per week") }, { value: "month", label: t("Per month") }]} />
    </div>
    <div className="fleet-secret-new-row">
      <HTMLSelect value={draft.mode} disabled={busy} aria-label={t("How it holds")} onChange={change("mode")}
        options={[{ value: "enforced", label: t("Enforced: each machine gets a share") }, { value: "advisory", label: t("Advisory: it warns") }]} />
      <HTMLSelect value={draft.reached} disabled={busy} aria-label={t("At its limit")} onChange={change("reached")}
        options={[{ value: "hold", label: t("Hold new runs for me") }, { value: "stop", label: t("Stop new runs") }]} />
    </div>
    <div className="fleet-secret-new-row fleet-secret-new-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />
      <Button type="submit" size="small" intent="primary" text={t(budget ? "Save" : "Add")} disabled={!ready} />
    </div>
  </form>;
}

/** One run that waits for a reached budget: the budget, what the run is, its machine; Continue anyway (confirmed beside it) or Stop. */
export function HoldRow({ hold, busy, onContinue, onStop }: { hold: FleetBudgetHoldView; busy: boolean; onContinue: () => void; onStop: () => void }) {
  const { t, locale } = useShellLanguage();
  const asked = new Date(hold.requestedAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  return <li className="secret-row fleet-approval-row fleet-hold-row" data-hold={hold.id}>
    <span className="secret-name"><code title={hold.budgetName}>{hold.budgetName}</code>
      <span className="secret-reference" title={hold.runId}>{hold.title ?? hold.runId}</span></span>
    <Tag minimal intent="warning">{t("Waiting")}</Tag>
    <span className="secret-meta">{t("{machine} asked {date}", { machine: hold.machine, date: asked })}</span>
    <span className="secret-actions">
      <PopoverNext placement="left" content={<div className="fleet-server-confirm">
        <p>{t("Continue this run on {machine} anyway?", { machine: hold.machine })}</p>
        <Button className="bp6-popover-dismiss" intent="warning" disabled={busy} onClick={onContinue}>{t("Continue anyway")}</Button></div>}>
        <Button size="small" intent="warning" disabled={busy}>{t("Continue anyway…")}</Button>
      </PopoverNext>
      <Button size="small" variant="minimal" disabled={busy} text={t("Stop")} onClick={onStop} />
    </span>
  </li>;
}

/** A notification not dismissed yet: what it says; Dismiss. */
function NotificationRow({ notification, busy, onDismiss }: { notification: FleetNotificationView; busy: boolean; onDismiss: () => void }) {
  const { t, locale } = useShellLanguage();
  const phrase = notificationPhrase(notification);
  const at = new Date(notification.createdAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  return <li className="secret-row fleet-notification-row" data-notification={notification.kind} data-problem={notification.problem ?? undefined}>
    <Tag minimal intent={notificationIntent(notification.severity)} icon={<AppIcon name={notification.kind === "health" ? "remote" : "usage"} size={12} />} />
    <span className="fleet-notification-text">{t(phrase.key, phrase.values)}</span>
    <span className="secret-meta">{at}</span>
    <span className="secret-actions"><Button size="small" variant="minimal" disabled={busy} text={t("Dismiss")} onClick={onDismiss} /></span>
  </li>;
}
