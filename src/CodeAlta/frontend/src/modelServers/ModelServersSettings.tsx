import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Button, Callout, Checkbox, PopoverNext, ProgressBar, Tab, Tabs, TabsExpander, Tag } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { SettingsPage } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import type { SettingsNotice } from "../settingsEditing";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import { FilterBar, type FilterFacet } from "../fleet/console/FilterBar";
import { facetCounts, filterRows, sortRows, type Facet, type TableView } from "../fleet/console/tableRows";
import { modelServersApi, ModelServersError, type ModelServersApi } from "./api";
import { updateInTurn } from "./modelServers";
import { activityOf, backendName, currentBuilds, formatBytes, formatSpeed, gpuSummary, isActive, loadingProgress, modelName, modelRows, progressOf, runningTasks,
  serverRows, serverStateOf, stateIntent, timeLeft, updateAvailable, type MachineStatus, type ModelRow, type ServerRow } from "./modelServers";
import type { ModelHostAction, ModelHostActionResult, ModelHostStatus, ServerDefinition } from "./types";
import { CopyServerDialog, DownloadDialog, DownloadToDialog, ImportDialog, PolicyDialog, ServerDialog, UpdateDialog } from "./ModelServerDialogs";

type TabId = "servers" | "models" | "builds";

const firstView = (column: string): TableView => ({ query: "", values: {}, sort: { column, direction: "asc" }, selected: null });

/** What the page reads of a machine without a notice: the result, or null when it could not be read. */
export type ReadAction = (machine: string, action: ModelHostAction) => Promise<ModelHostActionResult | null>;

/** What the page does with a machine: an action on its model host, its result, and the notice it gives. */
export type RunAction = (machine: string, action: ModelHostAction, success?: MessageKey, values?: Readonly<Record<string, string | number>>) => Promise<ModelHostActionResult | null>;

type Dialog =
  | { kind: "server"; machine: string | null; server: ServerDefinition | null; model?: string | null }
  | { kind: "download"; machine: string | null; model?: string | null }
  | { kind: "policy"; machines: readonly string[] }
  | { kind: "update"; mode: "now" | "at"; machines: readonly string[] }
  | { kind: "copy"; machine: string; server: ServerDefinition }
  | { kind: "downloadTo"; models: readonly string[] }
  | { kind: "import"; machine: string | null };

/** The rows checked in each tab, for the actions on several rows at once. */
type Checked = Readonly<Record<TabId, readonly string[]>>;

/**
 * Settings > Fleet > Model servers: the llama.cpp servers of this computer and of the paired machines, the models on
 * their disks and the builds of llama.cpp they run, as tables that sort and filter as the machines do. Each machine is
 * read through the host, which keeps the connections and the keys.
 */
export function ModelServersSettings({ epoch, api: given, onDiscover }: { epoch: string | null; api?: ModelServersApi; onDiscover?: () => void }) {
  const { t } = useShellLanguage();
  const api = useMemo(() => given ?? (epoch ? modelServersApi(epoch) : null), [given, epoch]);
  const [machines, setMachines] = useState<readonly MachineStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [tab, setTab] = useState<TabId>("servers");
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [views, setViews] = useState<Readonly<Record<TabId, TableView>>>({ servers: firstView("name"), models: firstView("size"), builds: firstView("machine") });
  const pending = useRef(new Map<string, number>());

  const readMachine = useCallback(async (id: string, signal?: AbortSignal) => {
    if (!api) return;
    try {
      const status = await api.status(id, signal);
      setMachines(current => current.map(entry => entry.machine.id === id ? { ...entry, status, problem: null, loading: false } : entry));
    } catch (error) {
      if (signal?.aborted) return;
      const problem = error instanceof Error ? error.message : String(error);
      setMachines(current => current.map(entry => entry.machine.id === id ? { ...entry, status: entry.status, problem, loading: false } : entry));
    }
  }, [api]);

  const reload = useCallback(async (signal?: AbortSignal) => {
    if (!api) return;
    setLoading(true);
    try {
      const listed = await api.machines(signal);
      setMachines(current => listed.map(machine => ({ machine, status: current.find(entry => entry.machine.id === machine.id)?.status ?? null, problem: null,
        loading: true })));
      await Promise.all(listed.filter(machine => machine.local || machine.state === "online").map(machine => readMachine(machine.id, signal)));
      setMachines(current => current.map(entry => entry.loading ? { ...entry, loading: false, problem: entry.problem ?? (entry.machine.state === "online" ? null : entry.machine.problem ?? null) } : entry));
    } catch (error) {
      if (!signal?.aborted) setNotice({ key: "The model servers could not be read.", intent: "danger", detail: error instanceof Error ? error.message : null });
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [api, readMachine]);

  useEffect(() => {
    if (!api) return;
    const controller = new AbortController();
    void reload(controller.signal);
    // A change of a machine reads that machine again, at most every second.
    void api.watch(controller.signal, event => {
      if (event.kind !== "changed" || pending.current.has(event.machine)) return;
      pending.current.set(event.machine, window.setTimeout(() => {
        pending.current.delete(event.machine);
        void readMachine(event.machine, controller.signal);
      }, 700));
    }).catch(() => { /* The page still reads on demand. */ });
    const timers = pending.current;
    return () => {
      controller.abort();
      for (const timer of timers.values()) window.clearTimeout(timer);
      timers.clear();
    };
  }, [api, reload, readMachine]);

  const run = useCallback<RunAction>(async (machine, action, success, values) => {
    if (!api) return null;
    setBusy(true);
    setNotice(null);
    try {
      const result = await api.act(machine, action);
      if (success) setNotice({ key: success, intent: "success", values });
      void readMachine(machine);
      return result;
    } catch (error) {
      setNotice({ key: "The model servers did not do it.", intent: "danger", detail: error instanceof ModelServersError || error instanceof Error ? error.message : String(error) });
      return null;
    } finally {
      setBusy(false);
    }
  }, [api, readMachine]);

  // What only reads (the log of a server) asks quietly: no busy state, no notice.
  const read = useCallback<ReadAction>(async (machine, action) => {
    if (!api) return null;
    try { return await api.act(machine, action); } catch { return null; }
  }, [api]);

  // Adds the provider of a server to this computer, so that its sessions use it.
  const useInSessions = useCallback(async (machine: string, server: ServerDefinition) => {
    if (!api) return;
    setBusy(true);
    setNotice(null);
    try {
      const results = await api.useInSessions(machine, server.id, server.name, ["local"]);
      const failed = results.find(result => result.problem);
      setNotice(failed ? { key: "The sessions do not use it yet.", intent: "warning", detail: failed.problem } : { key: "The sessions of this computer use it.", intent: "success" });
    } catch (error) {
      setNotice({ key: "The sessions do not use it yet.", intent: "warning", detail: error instanceof Error ? error.message : null });
    } finally {
      setBusy(false);
    }
  }, [api]);

  const managed = machines.filter(entry => entry.machine.manages && entry.status);
  const [checked, setChecked] = useState<Checked>({ servers: [], models: [], builds: [] });
  const check = (id: TabId) => (keys: readonly string[]) => setChecked(current => ({ ...current, [id]: keys }));
  // Update now: one machine after the other, to one build for all or the newest of each channel; the first that fails stops the others.
  const [updating, setUpdating] = useState(false);
  const updateNow = async (ids: readonly string[], build: string | undefined, models: boolean) => {
    if (!api) return;
    setUpdating(true);
    const names = new Map(machines.map(entry => [entry.machine.id, entry.machine.name]));
    const stoppedAt = await updateInTurn(ids,
      async machine => (await run(machine, { action: "updateNow", build, models }, "The update started."))?.task ?? null,
      async (machine, task) => (await api.status(machine).catch(() => null))?.tasks.find(item => item.id === task) ?? null);
    setUpdating(false);
    setNotice(stoppedAt === null ? { key: ids.length > 1 ? "All the machines are updated." : "The machine is updated.", intent: "success" }
      : { key: "The update stopped at {name}: the machines after it were not updated.", intent: "warning", values: { name: names.get(stoppedAt) ?? stoppedAt } });
  };
  const view = views[tab];
  const setView = (part: Partial<TableView>) => setViews(current => ({ ...current, [tab]: { ...current[tab], ...part } }));
  const reachable = machines.filter(entry => entry.status);
  const unreachable = machines.filter(entry => !entry.status && !entry.loading);

  return <SettingsPage className="model-servers-settings" label={t("Model servers")} group="Fleet" title="Model servers"
    description="The llama.cpp servers of this computer and of your machines: the builds they run, the models on their disks, and the sessions that use them."
    notice={notice} loading={loading && machines.length === 0} busy={busy} onReload={() => void reload()}
    actions={<>
      <Button intent="primary" icon={<AppIcon name="plus" size={14} />} text={t("Add a model server…")} disabled={managed.length === 0 || busy}
        onClick={() => setDialog({ kind: "server", machine: null, server: null })} />
      <Button icon={<AppIcon name="download" size={14} />} text={t("Download a model…")} disabled={managed.length === 0 || busy}
        onClick={() => setDialog({ kind: "download", machine: null })} />
      {onDiscover && <Button variant="minimal" icon={<AppIcon name="search" size={14} />} text={t("Discover models")} onClick={onDiscover} />}
    </>}>
    {unreachable.length > 0 && <p className="model-servers-unreachable muted-text"><AppIcon name="remote" size={13} />{t("Not reached now:")}
      {unreachable.map((entry, index) => <span key={entry.machine.id}>{index > 0 ? ", " : " "}<span className="model-servers-unreachable-name"
        title={entry.problem ?? t(entry.machine.state === "connecting" ? "Connecting…" : "Offline")}>{entry.machine.name}</span></span>)}</p>}
    {reachable.flatMap(entry => entry.status!.warnings.map(warning => <Callout key={`${entry.machine.id}:${warning}`} intent="warning" compact icon={<AppIcon name="warning" size={16} />}>
      <strong>{entry.machine.name}</strong>: {warning}</Callout>))}
    <Tabs id="model-servers-tabs" selectedTabId={tab} onChange={id => setTab(id as TabId)} renderActiveTabPanelOnly large={false}>
      <Tab id="servers" title={t("Servers")} panel={<ServersTab machines={reachable} view={views.servers} onView={setView} busy={busy} run={run} read={read} onUse={useInSessions}
        onEdit={(machine, server) => setDialog({ kind: "server", machine, server })} onCopy={(machine, server) => setDialog({ kind: "copy", machine, server })}
        checked={checked.servers} onCheck={check("servers")} />} />
      <Tab id="models" title={t("Models")} panel={<ModelsTab machines={reachable} view={views.models} onView={setView} busy={busy} run={run}
        onServe={(machine, model) => setDialog({ kind: "server", machine, server: null, model })} onDownloadTo={models => setDialog({ kind: "downloadTo", models })}
        checked={checked.models} onCheck={check("models")} />} />
      <Tab id="builds" title="llama.cpp" panel={<BuildsTab machines={reachable} view={views.builds} onView={setView} busy={busy || updating} run={run}
        onPolicy={ids => setDialog({ kind: "policy", machines: ids })} onUpdate={(mode, ids) => setDialog({ kind: "update", mode, machines: ids })}
        checked={checked.builds} onCheck={check("builds")} />} />
      <TabsExpander />
      {tab === "servers" && <Button size="small" variant="minimal" icon={<AppIcon name="code" size={13} />} text={t("Import a command line…")} disabled={managed.length === 0 || busy}
        onClick={() => setDialog({ kind: "import", machine: null })} />}
      {tab === "builds" && reachable.length > 1 && <>
        <Button size="small" variant="minimal" icon={updating ? <ActivitySpinner size={13} /> : <AppIcon name="refresh" size={13} />} text={t("Update all now…")} disabled={busy || updating || managed.length === 0}
          onClick={() => setDialog({ kind: "update", mode: "now", machines: managed.map(entry => entry.machine.id) })} />
        <Button size="small" variant="minimal" icon={<AppIcon name="timer" size={13} />} text={t("Update all at…")} disabled={busy || managed.length === 0}
          onClick={() => setDialog({ kind: "update", mode: "at", machines: managed.map(entry => entry.machine.id) })} />
        <Button size="small" variant="minimal" icon={<AppIcon name="settings" size={13} />} text={t("Policy of all…")} disabled={busy || managed.length === 0}
          onClick={() => setDialog({ kind: "policy", machines: managed.map(entry => entry.machine.id) })} />
      </>}
    </Tabs>
    {dialog?.kind === "server" && <ServerDialog api={api!} machines={machines} machine={dialog.machine} server={dialog.server} model={dialog.model ?? null} run={run}
      onClose={() => setDialog(null)} />}
    {dialog?.kind === "download" && <DownloadDialog api={api!} machines={machines} machine={dialog.machine} model={dialog.model ?? null} run={run} onClose={() => setDialog(null)} />}
    {dialog?.kind === "import" && <ImportDialog machines={machines} machine={dialog.machine} run={run} onImported={(machine, server) => setDialog({ kind: "server", machine, server })}
      onClose={() => setDialog(null)} />}
    {dialog?.kind === "policy" && <PolicyDialog machines={machines.filter(entry => dialog.machines.includes(entry.machine.id))} run={run} onClose={() => setDialog(null)} />}
    {/* In the order the machines were given, the order of the table: Update now updates them one after the other in it. */}
    {dialog?.kind === "update" && <UpdateDialog machines={inOrder(machines, dialog.machines)} mode={dialog.mode} run={run} onNow={updateNow}
      onClose={() => setDialog(null)} />}
    {dialog?.kind === "copy" && <CopyServerDialog machines={machines} from={dialog.machine} server={dialog.server} run={run} onClose={() => setDialog(null)} />}
    {dialog?.kind === "downloadTo" && <DownloadToDialog machines={machines} models={dialog.models} run={run} onClose={() => setDialog(null)} />}
  </SettingsPage>;
}



function State({ state, progress }: { state: string; progress?: number | null }) {
  const { t } = useShellLanguage();
  const label = stateLabel(state);
  return <Tag minimal round intent={stateIntent(state)} className="model-servers-state">
    {t(label)}{typeof progress === "number" && progress > 0 && progress < 1 ? ` ${Math.round(progress * 100)} %` : ""}
  </Tag>;
}

/** The words of a state. */
export function stateLabel(state: string): MessageKey {
  switch (state) {
    case "ready": return "Ready";
    case "loading": return "Loading";
    case "starting": return "Starting";
    case "waiting": return "Waiting";
    case "stopped": return "Stopped";
    case "failed": return "Failed";
    case "updating": return "Updating";
    case "loaded": return "Loaded";
    case "unloaded": return "Not loaded";
    case "sleeping": return "Sleeping";
    case "missing": return "Not on disk";
    case "downloading": return "Downloading";
    case "checking": return "Checking";
    case "queued": return "Queued";
    case "paused": return "Paused";
    case "done": return "Done";
    case "cancelled": return "Cancelled";
    default: return "Unknown";
  }
}

function ServersTab({ machines, view, onView, busy, run, read, onUse, onEdit, onCopy, checked, onCheck }: {
  machines: readonly MachineStatus[]; view: TableView; onView: (part: Partial<TableView>) => void; busy: boolean; run: RunAction; read: ReadAction;
  onUse: (machine: string, server: ServerDefinition) => Promise<void>;
  onEdit: (machine: string, server: ServerDefinition) => void;
  onCopy: (machine: string, server: ServerDefinition) => void;
  checked: readonly string[]; onCheck: (keys: readonly string[]) => void;
}) {
  const { t } = useShellLanguage();
  const rows = useMemo(() => serverRows(machines), [machines]);
  const facets = useMemo<Facet<ServerRow>[]>(() => [
    { id: "state", values: row => [serverStateOf(row.server)] },
    { id: "machine", values: row => [row.machine.name] },
    { id: "backend", values: row => [row.server.definition.backend] },
    { id: "model", values: row => row.server.definition.models.map(model => modelName(model.model)) },
  ], []);
  const shown = useMemo(() => filterRows(rows, facets, view.values, view.query, row => [row.server.definition.name, row.machine.name, row.server.definition.backend,
    ...row.server.definition.models.map(model => model.model), row.server.status.reason].filter(Boolean).join(" ")), [rows, facets, view]);
  const options = (id: string, label: (value: string) => string) => [...facetCounts(rows, facets.find(facet => facet.id === id)!)].map(([value, count]) => ({ value, label: label(value), count }));
  const filters: FilterFacet[] = [
    { id: "state", label: "State", icon: "issueOpen", options: options("state", value => t(stateLabel(value))) },
    { id: "machine", label: "Machine", icon: "remote", options: options("machine", value => value) },
    { id: "backend", label: "GPU family", icon: "computer", options: options("backend", backendName) },
    { id: "model", label: "Model", icon: "model", options: options("model", value => value) },
  ];
  const picked = shown.filter(row => checked.includes(row.key));
  const each = async (action: (row: ServerRow) => ModelHostAction | null) => {
    for (const row of picked) { const asked = action(row); if (asked) await run(row.machine.id, asked); }
    onCheck([]);
  };
  const columns: DataColumn<ServerRow>[] = [
    pickColumn<ServerRow>(row => row.key, checked, onCheck, row => t("Select {name}", { name: row.server.definition.name }), row => row.machine.manages),
    { id: "state", label: "State", width: "8rem", sort: row => serverStateOf(row.server), cell: row => <State state={serverStateOf(row.server)} progress={loadingProgress(row.server)} /> },
    { id: "name", label: "Name", sort: row => row.server.definition.name, cell: row => <span className="model-servers-name"><AppIcon name="server" size={14} />
      <strong>{row.server.definition.name}</strong>{row.server.definition.purpose !== "chat" && <Tag minimal>{t(row.server.definition.purpose === "embedding" ? "Embeddings" : "Rerank")}</Tag>}</span> },
    { id: "machine", label: "Machine", width: "9rem", sort: row => row.machine.name, cell: row => row.machine.name },
    { id: "models", label: "Models", sort: row => row.server.definition.models.length, cell: row => <ModelsCell row={row} /> },
    { id: "gpus", label: "GPUs", width: "13rem", secondary: true, sort: row => row.server.definition.backend, cell: row => <span className="model-servers-gpus">
      {serverGpus(row) || gpuSummary(row.status.hardware)} · {backendName(row.server.definition.backend)}</span> },
    { id: "activity", label: "Activity", width: "10rem", secondary: true, sort: row => activityOf(row.server).busy, cell: row => {
      const activity = activityOf(row.server);
      return activity.total === 0 ? <span className="muted-text">—</span> : <span>{t("{busy} of {total} requests", { busy: activity.busy, total: activity.total })}
        {activity.tokensPerSecond ? <span className="muted-text"> · {t("{count} tokens/s", { count: Math.round(activity.tokensPerSecond) })}</span> : null}</span>;
    } },
    { id: "build", label: "llama.cpp", width: "8rem", secondary: true, sort: row => row.server.status.buildTag, cell: row => <span>{row.server.status.buildTag ?? "—"}
      {updateAvailable(row.status) && <Tag minimal intent="primary" className="model-servers-update">{t("update")}</Tag>}</span> },
    { id: "reach", label: "Reached by", width: "9rem", secondary: true, sort: row => row.server.definition.access,
      cell: row => t(row.server.definition.access === "my-computers" ? "Your computers" : "This machine") },
  ];
  return <section className="fleet-table-section" aria-label={t("Servers")}>
    <FilterBar label={t("Filter the model servers")} query={view.query} onQuery={query => onView({ query })} facets={filters} values={view.values}
      onValues={values => onView({ values })} shown={shown.length} total={rows.length} />
    {picked.length > 0 && <SelectionBar count={picked.length} total={shown.filter(row => row.machine.manages).length}
      onAll={() => onCheck(shown.filter(row => row.machine.manages).map(row => row.key))} onClear={() => onCheck([])}>
      <Button size="small" icon={<AppIcon name="play" size={13} />} text={t("Start")} disabled={busy}
        onClick={() => void each(row => row.server.status.state === "stopped" || row.server.status.state === "failed" ? { action: "controlServer", id: row.server.definition.id, verb: "start" } : null)} />
      <Button size="small" icon={<AppIcon name="stop" size={13} />} text={t("Stop")} disabled={busy}
        onClick={() => void each(row => row.server.status.state !== "stopped" ? { action: "controlServer", id: row.server.definition.id, verb: "stop" } : null)} />
      <Button size="small" icon={<AppIcon name="refresh" size={13} />} text={t("Restart")} disabled={busy}
        onClick={() => void each(row => ({ action: "controlServer", id: row.server.definition.id, verb: "restart" }))} />
      <RemoveButton busy={busy} question={t("Remove the {count} servers selected?", { count: picked.length })} detail={t("They stop and their settings go. Their models stay on the disk.")}
        onConfirm={() => void each(row => ({ action: "removeServer", id: row.server.definition.id }))} />
    </SelectionBar>}
    <DataTable label={t("Model servers")} rows={shown} columns={columns} rowKey={row => row.key} sort={view.sort} onSort={sort => onView({ sort })}
      selected={view.selected} onSelect={selected => onView({ selected })} rowState={row => rowMark(stateIntent(serverStateOf(row.server)))}
      empty={<EmptyState rows={rows.length} text="No model server yet: add one, or import the command line of a llama-server you run." />}
      details={row => <ServerDetails row={row} busy={busy} run={run} read={read} onUse={() => void onUse(row.machine.id, row.server.definition)} onEdit={() => onEdit(row.machine.id, row.server.definition)}
        onCopy={() => onCopy(row.machine.id, row.server.definition)} />} />
  </section>;
}

/** The machines named, in the order they are named. */
function inOrder(machines: readonly MachineStatus[], ids: readonly string[]): MachineStatus[] {
  return ids.flatMap(id => machines.filter(entry => entry.machine.id === id));
}

/** The column of the boxes that check rows, for the actions on several rows at once; a row that may not be changed has none. */
function pickColumn<T>(key: (row: T) => string, checked: readonly string[], onCheck: (keys: readonly string[]) => void, label: (row: T) => string,
  enabled: (row: T) => boolean): DataColumn<T> {
  return { id: "pick", label: "Select", hiddenLabel: true, width: "2.4rem", cell: row => enabled(row)
    ? <Checkbox className="model-servers-pick" checked={checked.includes(key(row))} aria-label={label(row)}
      onChange={event => onCheck(event.currentTarget.checked ? [...checked, key(row)] : checked.filter(item => item !== key(row)))} />
    : null };
}

/** What can be done to the rows checked, above the table. */
function SelectionBar({ count, total, onAll, onClear, children }: { count: number; total: number; onAll: () => void; onClear: () => void; children: ReactNode }) {
  const { t } = useShellLanguage();
  return <div className="model-servers-selection" role="toolbar" aria-label={t("Actions on the rows selected")}>
    <strong>{t("{count} selected", { count })}</strong>
    {children}
    <span className="model-servers-selection-end">
      {count < total && <Button size="small" variant="minimal" text={t("Select all {count}", { count: total })} onClick={onAll} />}
      <Button size="small" variant="minimal" text={t("Clear the selection")} onClick={onClear} />
    </span>
  </div>;
}

/** The mark at the start of a row, as the tables of the Fleet console draw it. */
function rowMark(intent: string): string | undefined {
  return intent === "success" ? "online" : intent === "warning" ? "connecting" : intent === "danger" ? "refused" : undefined;
}

function serverGpus(row: ServerRow): string {
  const build = row.status.builds.find(item => item.current && item.backend === row.server.definition.backend);
  const named = row.server.definition.devices.length > 0 ? row.server.definition.devices : [...new Set(row.server.definition.models.flatMap(model => model.devices))];
  // A server that names no GPU runs on the discrete GPUs its build sees (LlamaBuilds.DiscreteDevices): not the GPU of the processor.
  const integrated = new Set(row.status.hardware.gpus.filter(gpu => gpu.integrated).map(gpu => gpu.name.replace(/\((TM|R)\)/giu, "").replace(/\s+/gu, " ").trim().toUpperCase()));
  const discrete = build?.devices.filter(device => !/^cpu/iu.test(device.name) && !/\b(Ryzen|Core\(TM\)|Core Ultra|Processor)\b/iu.test(device.description)
    && !integrated.has(device.description.replace(/\((TM|R)\)/giu, "").replace(/\s+/gu, " ").trim().toUpperCase())).map(device => device.name) ?? [];
  const devices = named.length > 0 ? named : discrete.length > 0 ? discrete : build?.devices.map(device => device.name) ?? [];
  if (!build || devices.length === 0) return "";
  const names = devices.map(device => build.devices.find(item => item.name === device)?.description.replace(/^(AMD|NVIDIA)\s+/iu, "") ?? device);
  const counts = new Map<string, number>();
  for (const name of names) counts.set(name, (counts.get(name) ?? 0) + 1);
  return [...counts].map(([name, count]) => count > 1 ? `${count}× ${name}` : name).join(" · ");
}

function ModelsCell({ row }: { row: ServerRow }) {
  const { t } = useShellLanguage();
  const models = row.server.definition.models;
  const loaded = row.server.status.models.filter(model => model.state === "loaded");
  const first = loaded[0]?.model ?? models[0]?.model;
  if (!first) return <span className="muted-text">—</span>;
  return <span className="model-servers-models" title={models.map(model => model.model).join("\n")}>{modelName(first)}
    {loaded.some(model => model.model === first) && <span className="muted-text"> ({t("loaded")})</span>}
    {models.length > 1 && <Tag minimal round className="model-servers-more">+{models.length - 1}</Tag>}</span>;
}

function ServerDetails({ row, busy, run, read, onUse, onEdit, onCopy }: { row: ServerRow; busy: boolean; run: RunAction; read: ReadAction; onUse: () => void; onEdit: () => void;
  onCopy: () => void }) {
  const { t, locale } = useShellLanguage();
  const [log, setLog] = useState<readonly string[] | null>(null);
  const [test, setTest] = useState<Readonly<{ passed: boolean; message: string }> | null>(null);
  const { server, machine } = row;
  const id = server.definition.id;
  const running = server.status.state !== "stopped" && server.status.state !== "waiting";
  const act = (verb: string, model?: string) => run(machine.id, { action: "controlServer", id, verb, model });
  const manage = machine.manages;
  useEffect(() => {
    let alive = true;
    void read(machine.id, { action: "serverLog", id, lines: 12 }).then(result => { if (alive) setLog(result?.log ?? []); });
    return () => { alive = false; };
    // The log is read once when the row opens, and again with Show the log.
  }, [machine.id, id, read]);
  return <div className="model-servers-details">
    {server.status.reason && <p className={`model-servers-reason${server.status.state === "failed" ? " is-failed" : ""}`}><AppIcon name={server.status.state === "failed" ? "error" : "info"} size={14} />{server.status.reason}</p>}
    <dl>
      <div><dt>{t("Address")}</dt><dd>{server.status.address ? <code>{server.status.address}</code> : "—"}<span className="muted-text"> · {t("port {port}", { port: server.definition.port })}</span></dd></div>
      <div><dt>{t("Build")}</dt><dd>{server.status.buildTag ?? currentBuilds(row.status.builds.filter(build => build.backend === server.definition.backend)) ?? "—"} · {backendName(server.definition.backend)}</dd></div>
      <div><dt>{t("Kind")}</dt><dd>{t(server.definition.kind === "several" ? "Several models, loaded on demand ({count} at once)" : "One model, always loaded",
        { count: server.definition.loadedAtOnce })}</dd></div>
      <div><dt>{t("Reached by")}</dt><dd>{t(server.definition.access === "my-computers" ? "This machine and your computers" : "This machine only")}</dd></div>
    </dl>
    <table className="model-servers-models-table">
      <thead><tr><th>{t("Model")}</th><th>{t("State")}</th><th>{t("Context")}</th><th>{t("Requests")}</th><th>{t("Speed")}</th><th aria-label={t("Actions")} /></tr></thead>
      <tbody>{server.definition.models.map(model => {
        const status = server.status.models.find(item => item.model === model.model);
        const state = status?.state ?? "unloaded";
        return <tr key={model.model}>
          <td><strong>{modelName(model.model)}</strong> <span className="muted-text">{model.model.split(":")[0]}</span>{status?.vision && <Tag minimal className="model-servers-vision">{t("vision")}</Tag>}</td>
          <td><State state={state} progress={status?.progress} /></td>
          <td>{status?.context ? new Intl.NumberFormat(locale).format(status.context) : model.context ? new Intl.NumberFormat(locale).format(model.context) : t("fit")}</td>
          <td>{status && status.totalSlots > 0 ? `${status.busySlots}/${status.totalSlots}` : "—"}</td>
          <td>{status?.tokensPerSecond ? t("{count} tokens/s", { count: Math.round(status.tokensPerSecond) }) : "—"}</td>
          <td className="model-servers-model-actions">{running && manage && server.definition.kind === "several" && (state === "loaded" || state === "sleeping"
            ? <Button size="small" variant="minimal" text={t("Unload")} disabled={busy} onClick={() => void act("unload", model.model)} />
            : state === "unloaded" ? <Button size="small" variant="minimal" text={t("Load")} disabled={busy} onClick={() => void act("load", model.model)} /> : null)}</td>
        </tr>;
      })}</tbody>
    </table>
    {test && <p className={`model-servers-test${test.passed ? "" : " is-failed"}`}><AppIcon name={test.passed ? "check" : "error"} size={14} />{test.message}</p>}
    {log && log.length > 0 && <pre className="model-servers-log" aria-label={t("The end of its log")}>{log.join("\n")}</pre>}
    {manage && <div className="machine-details-actions">
      {running
        ? <><Button size="small" icon={<AppIcon name="stop" size={14} />} text={t("Stop")} disabled={busy} onClick={() => void act("stop")} />
          <Button size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Restart")} disabled={busy} onClick={() => void act("restart")} />
          <Button size="small" icon={<AppIcon name="play" size={14} />} text={t("Test")} disabled={busy}
            onClick={() => void run(machine.id, { action: "testServer", id }).then(result => setTest(result?.test ?? null))} /></>
        : <Button size="small" intent="primary" icon={<AppIcon name="play" size={14} />} text={t("Start")} disabled={busy} onClick={() => void act("start")} />}
      <Button size="small" icon={<AppIcon name="edit" size={14} />} text={t("Change…")} disabled={busy} onClick={onEdit} />
      <Button size="small" variant="minimal" icon={<AppIcon name="copy" size={14} />} text={t("Copy to…")} disabled={busy} onClick={onCopy} />
      {server.definition.purpose === "chat" && (row.machine.local || server.definition.access === "my-computers")
        && <Button size="small" variant="minimal" icon={<AppIcon name="provider" size={14} />} text={t("Use in sessions")} disabled={busy} onClick={onUse} />}
      <Button size="small" variant="minimal" icon={<AppIcon name="logs" size={14} />} text={t("Show the log")} disabled={busy}
        onClick={() => void read(machine.id, { action: "serverLog", id, lines: 200 }).then(result => setLog(result?.log ?? []))} />
      <RemoveButton busy={busy} question={t("Remove {name}?", { name: server.definition.name })} detail={t("It stops and its settings go. Its models stay on the disk.")}
        onConfirm={() => void run(machine.id, { action: "removeServer", id }, "The server was removed.")} />
    </div>}
    {!manage && <p className="muted-text">{t("This computer uses this machine's servers; the computers that manage it change them.")}</p>}
  </div>;
}

function RemoveButton({ busy, question, detail, onConfirm, label = "Remove" }: { busy: boolean; question: string; detail: string; onConfirm: () => void; label?: MessageKey }) {
  const { t } = useShellLanguage();
  return <PopoverNext placement="top" content={<div className="machine-forget">
    <p>{question}</p>
    <p className="muted-text">{detail}</p>
    <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={onConfirm}>{t(label)}</Button></div>}>
    <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={busy} text={t(label)} />
  </PopoverNext>;
}

function EmptyState({ rows, text }: { rows: number; text: MessageKey }) {
  const { t } = useShellLanguage();
  return <span className="muted-text">{t(rows === 0 ? text : "Nothing matches these filters.")}</span>;
}

function ModelsTab({ machines, view, onView, busy, run, onServe, onDownloadTo, checked, onCheck }: {
  machines: readonly MachineStatus[]; view: TableView; onView: (part: Partial<TableView>) => void; busy: boolean; run: RunAction;
  onServe: (machine: string, model: string) => void; onDownloadTo: (models: readonly string[]) => void;
  checked: readonly string[]; onCheck: (keys: readonly string[]) => void;
}) {
  const { t, locale } = useShellLanguage();
  const rows = useMemo(() => modelRows(machines), [machines]);
  const facets = useMemo<Facet<ModelRow>[]>(() => [
    { id: "machine", values: row => [row.machine.name] },
    { id: "use", values: row => [row.download && isActive(row.download) ? "downloading" : row.usedBy.length > 0 ? "used" : "unused"] },
    { id: "publisher", values: row => [row.id.split("/")[0]] },
  ], []);
  const shown = useMemo(() => filterRows(rows, facets, view.values, view.query, row => [row.id, row.machine.name, ...row.usedBy].join(" ")), [rows, facets, view]);
  const options = (id: string, label: (value: string) => string) => [...facetCounts(rows, facets.find(facet => facet.id === id)!)].map(([value, count]) => ({ value, label: label(value), count }));
  const useLabels: Readonly<Record<string, MessageKey>> = { used: "Used by a server", unused: "Used by no server", downloading: "Downloading" };
  const filters: FilterFacet[] = [
    { id: "machine", label: "Machine", icon: "remote", options: options("machine", value => value) },
    { id: "use", label: "Use", icon: "server", options: options("use", value => t(useLabels[value] ?? "Unknown")) },
    { id: "publisher", label: "Publisher", icon: "user", options: options("publisher", value => value) },
  ];
  const picked = shown.filter(row => checked.includes(row.key));
  const removable = picked.filter(row => row.model && row.machine.manages);
  const columns: DataColumn<ModelRow>[] = [
    pickColumn<ModelRow>(row => row.key, checked, onCheck, row => t("Select {name}", { name: `${modelName(row.id)} · ${row.machine.name}` }), row => !!row.model),
    { id: "name", label: "Model", sort: row => modelName(row.id), cell: row => <span className="model-servers-name"><AppIcon name="model" size={14} />
      <span><strong>{modelName(row.id)}</strong><span className="muted-text model-servers-repo">{row.id.split(":")[0]}</span></span>
      {row.model?.vision && <Tag minimal className="model-servers-vision">{t("vision")}</Tag>}</span> },
    { id: "machine", label: "Machine", width: "9rem", sort: row => row.machine.name, cell: row => row.machine.name },
    { id: "size", label: "Size", width: "7rem", align: "end", sort: row => -(row.model?.sizeBytes ?? row.download?.total ?? 0),
      cell: row => formatBytes(row.model?.sizeBytes ?? row.download?.total ?? null, locale) },
    { id: "used", label: "Used by", width: "11rem", secondary: true, sort: row => row.usedBy.join(", "),
      cell: row => row.usedBy.length > 0 ? row.usedBy.join(", ") : <span className="muted-text">{t("No server")}</span> },
    { id: "state", label: "State", width: "15rem", sort: row => row.download ? 0 : 1, cell: row => row.download ? <DownloadCell row={row} /> : <State state="ready" /> },
  ];
  const totals = machines.filter(entry => entry.status && (entry.status.models.length > 0 || entry.status.downloads.some(isActive)));
  return <section className="fleet-table-section" aria-label={t("Models")}>
    <div className="model-servers-disks">{totals.map(entry => <DiskBar key={entry.machine.id} entry={entry} busy={busy} run={run} />)}</div>
    <FilterBar label={t("Filter the models")} query={view.query} onQuery={query => onView({ query })} facets={filters} values={view.values}
      onValues={values => onView({ values })} shown={shown.length} total={rows.length} />
    {picked.length > 0 && <SelectionBar count={picked.length} total={shown.filter(row => row.model).length} onAll={() => onCheck(shown.filter(row => row.model).map(row => row.key))}
      onClear={() => onCheck([])}>
      <Button size="small" icon={<AppIcon name="download" size={13} />} text={t("Download to…")} disabled={busy}
        onClick={() => onDownloadTo([...new Set(picked.map(row => row.id))])} />
      {removable.length > 0 && <RemoveButton busy={busy} question={t("Remove the {count} models selected?", { count: removable.length })}
        detail={t("The servers that use them stop first. They free {size}.", { size: formatBytes(removable.reduce((sum, row) => sum + (row.model?.freedBytes ?? 0), 0), locale) })}
        onConfirm={() => void (async () => { for (const row of removable) await run(row.machine.id, { action: "removeModel", model: row.id, stop: true }); onCheck([]); })()} />}
    </SelectionBar>}
    <DataTable label={t("Models")} rows={shown} columns={columns} rowKey={row => row.key} sort={view.sort} onSort={sort => onView({ sort })}
      selected={view.selected} onSelect={selected => onView({ selected })} rowState={row => row.download?.state === "failed" ? "refused" : row.download ? "connecting" : row.usedBy.length > 0 ? "online" : undefined}
      empty={<EmptyState rows={rows.length} text="No model on these machines yet: download one, or discover models on Hugging Face." />}
      details={row => <ModelDetails row={row} busy={busy} run={run} onServe={() => onServe(row.machine.id, row.id)} />} />
  </section>;
}

function DiskBar({ entry, busy, run }: { entry: MachineStatus; busy: boolean; run: RunAction }) {
  const { t, locale } = useShellLanguage();
  const status = entry.status!;
  const used = status.modelsBytes;
  const free = status.freeBytes;
  const total = used + free;
  const unused = status.models.filter(model => status.servers.every(server => server.definition.models.every(item => item.model.toLowerCase() !== model.id.toLowerCase())));
  const reclaimable = unused.reduce((sum, model) => sum + model.freedBytes, 0);
  return <div className="model-servers-disk">
    <div className="model-servers-disk-title"><strong>{entry.machine.name}</strong>
      <span className="muted-text">{t("models {used} · free {free}", { used: formatBytes(used, locale), free: formatBytes(free, locale) })}</span></div>
    <ProgressBar value={total > 0 ? used / total : 0} stripes={false} animate={false} intent={free < 20e9 ? "warning" : "primary"} />
    <div className="model-servers-disk-foot muted-text" title={status.modelsFolder}>{status.modelsFolder}</div>
    {unused.length > 0 && entry.machine.manages && <PopoverNext placement="bottom-start" content={<div className="machine-forget model-servers-cleanup">
      <p>{t("Remove the {count} models no server uses?", { count: unused.length })}</p>
      <ul>{unused.map(model => <li key={model.id}>{modelName(model.id)} <span className="muted-text">{formatBytes(model.freedBytes, locale)}</span></li>)}</ul>
      <Button className="bp6-popover-dismiss" intent="danger" disabled={busy}
        onClick={() => void (async () => { for (const model of unused) await run(entry.machine.id, { action: "removeModel", model: model.id }); })()}>
        {t("Remove them ({size})", { size: formatBytes(reclaimable, locale) })}</Button></div>}>
      <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={13} />} disabled={busy} text={t("Clean up… {size}", { size: formatBytes(reclaimable, locale) })} />
    </PopoverNext>}
  </div>;
}

function DownloadCell({ row }: { row: ModelRow }) {
  const { t, locale } = useShellLanguage();
  const download = row.download!;
  if (download.state === "failed") return <span className="model-servers-failed" title={download.error}><State state="failed" /> {download.error}</span>;
  const left = timeLeft(download);
  return <span className="model-servers-progress">
    <ProgressBar value={download.state === "queued" ? undefined : progressOf(download)} intent="primary" stripes={download.state === "downloading"} animate={download.state === "downloading"} />
    <span className="muted-text">{download.state === "downloading"
      ? [`${formatBytes(download.done, locale)} / ${formatBytes(download.total, locale)}`, formatSpeed(download.bytesPerSecond, locale), left ? t("{time} left", { time: left }) : ""].filter(Boolean).join(" · ")
      : t(stateLabel(download.state))}</span>
  </span>;
}

function ModelDetails({ row, busy, run, onServe }: { row: ModelRow; busy: boolean; run: RunAction; onServe: () => void }) {
  const { t, locale } = useShellLanguage();
  const download = row.download;
  const model = row.model;
  const manage = row.machine.manages;
  return <div className="model-servers-details">
    {download?.error && <p className="model-servers-reason is-failed"><AppIcon name="error" size={14} />{download.error}</p>}
    <dl>
      <div><dt>{t("Model")}</dt><dd><code>{row.id}</code></dd></div>
      {model && <div><dt>{t("Revision")}</dt><dd><code>{model.revision.slice(0, 12)}</code></dd></div>}
      {model && <div><dt>{t("Frees")}</dt><dd>{formatBytes(model.freedBytes, locale)}{model.freedBytes < model.sizeBytes && <span className="muted-text"> · {t("its other parts are shared")}</span>}</dd></div>}
      {model?.modelPath && <div><dt>{t("Path")}</dt><dd><code className="model-servers-path">{model.modelPath}</code></dd></div>}
      {model?.measured && <div><dt>{t("Measured fit")}</dt><dd>{model.measured.problem ? <span className="muted-text">{model.measured.problem}</span> : <>
        {model.measured.context ? t("Up to {count} tokens of context", { count: new Intl.NumberFormat(locale).format(model.measured.context) }) : t("Context: as the model asks")}
        {" · "}{model.measured.gpuLayers === -1 || model.measured.gpuLayers === undefined ? t("all its layers on the GPUs") : t("{count} layers on the GPUs", { count: model.measured.gpuLayers })}
        <span className="muted-text"> · {model.measured.build} {backendName(model.measured.backend)}</span>
        {model.measured.devices.length > 0 && <span className="model-servers-devices">{model.measured.devices.map(device => <span key={device.device}>
          <strong>{device.device}</strong> {t("model {model}, context {context}, buffers {compute}", { model: formatBytes(device.modelBytes, locale),
            context: formatBytes(device.contextBytes, locale), compute: formatBytes(device.computeBytes, locale) })}</span>)}</span>}</>}</dd></div>}
    </dl>
    {model && <table className="model-servers-models-table"><tbody>{model.files.map(file => <tr key={file.path}><td><code>{file.path}</code></td>
      <td>{t(file.kind === "mmproj" ? "vision projector" : "model")}{file.shared && <span className="muted-text"> · {t("shared")}</span>}</td><td className="numeric">{formatBytes(file.size, locale)}</td></tr>)}</tbody></table>}
    {manage && <div className="machine-details-actions">
      {model && <Button size="small" intent="primary" icon={<AppIcon name="server" size={14} />} text={t("Serve…")} disabled={busy} onClick={onServe} />}
      {model && row.status.builds.some(build => build.current) && <Button size="small" variant="minimal" icon={<AppIcon name="computer" size={14} />}
        text={t(model.measured ? "Measure again" : "Measure the fit")} disabled={busy}
        onClick={() => void run(row.machine.id, { action: "measureFit", model: row.id }, "The fit was measured.")} />}
      {download?.state === "downloading" || download?.state === "queued" ? <>
        <Button size="small" icon={<AppIcon name="pause" size={14} />} text={t("Pause")} disabled={busy} onClick={() => void run(row.machine.id, { action: "controlDownload", id: download.id, verb: "pause" })} />
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="close" size={14} />} text={t("Cancel")} disabled={busy}
          onClick={() => void run(row.machine.id, { action: "controlDownload", id: download.id, verb: "cancel" })} /></>
        : download && (download.state === "paused" || download.state === "failed")
          ? <Button size="small" icon={<AppIcon name="play" size={14} />} text={t("Resume")} disabled={busy} onClick={() => void run(row.machine.id, { action: "controlDownload", id: download.id, verb: "resume" })} />
          : null}
      {model && <RemoveButton busy={busy} question={t("Remove {name} from {machine}?", { name: modelName(row.id), machine: row.machine.name })}
        detail={row.usedBy.length > 0 ? t("{servers} serve it: they stop first. It frees {size}.", { servers: row.usedBy.join(", "), size: formatBytes(model.freedBytes, locale) })
          : t("It frees {size}.", { size: formatBytes(model.freedBytes, locale) })}
        onConfirm={() => void run(row.machine.id, { action: "removeModel", model: row.id, stop: true }, "The model was removed.")} />}
    </div>}
  </div>;
}

/** A machine as a row of the llama.cpp tab. */
type BuildRow = Readonly<{ key: string; entry: MachineStatus; status: ModelHostStatus }>;

function BuildsTab({ machines, view, onView, busy, run, onPolicy, onUpdate, checked, onCheck }: {
  machines: readonly MachineStatus[]; view: TableView; onView: (part: Partial<TableView>) => void; busy: boolean; run: RunAction;
  onPolicy: (machines: readonly string[]) => void; onUpdate: (mode: "now" | "at", machines: readonly string[]) => void;
  checked: readonly string[]; onCheck: (keys: readonly string[]) => void;
}) {
  const { t, locale } = useShellLanguage();
  const time = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }), [locale]);
  const rows = useMemo<BuildRow[]>(() => machines.map(entry => ({ key: entry.machine.id, entry, status: entry.status! })), [machines]);
  const shown = useMemo(() => filterRows(rows, [], {}, view.query, row => [row.entry.machine.name, gpuSummary(row.status.hardware, true), currentBuilds(row.status.builds)].join(" ")), [rows, view]);
  const checkedRows = shown.filter(row => checked.includes(row.key));
  const columns: DataColumn<BuildRow>[] = [
    pickColumn<BuildRow>(row => row.key, checked, onCheck, row => t("Select {name}", { name: row.entry.machine.name }), row => row.entry.machine.manages),
    { id: "machine", label: "Machine", width: "10rem", sort: row => row.entry.machine.name, cell: row => <strong>{row.entry.machine.name}</strong> },
    { id: "gpus", label: "GPUs", sort: row => gpuSummary(row.status.hardware), cell: row => <span className="model-servers-gpus">{gpuSummary(row.status.hardware) || t("No GPU")}
      {row.status.hardware.gpus.filter(gpu => gpu.architecture && !gpu.integrated).length > 0 && <span className="muted-text"> ({[...new Set(row.status.hardware.gpus.filter(gpu => !gpu.integrated).map(gpu => gpu.architecture).filter(Boolean))].join(", ")})</span>}</span> },
    { id: "build", label: "Build", width: "14rem", sort: row => currentBuilds(row.status.builds), cell: row => {
      const running = runningTasks(row.status);
      if (running.length > 0) return <span className="model-servers-progress"><ActivitySpinner size={12} /> {running[0].message ?? running[0].title}</span>;
      return currentBuilds(row.status.builds) || <span className="muted-text">{t("Not installed")}</span>;
    } },
    { id: "latest", label: "Latest", width: "9rem", secondary: true, sort: row => row.status.latest?.build, cell: row => row.status.latest
      ? <span>{row.status.latest.tag}{row.status.latest.version && <span className="muted-text"> ({row.status.latest.version})</span>}
        {updateAvailable(row.status) && <Tag minimal intent="primary" className="model-servers-update">{t("update")}</Tag>}</span> : <span className="muted-text">—</span> },
    { id: "updates", label: "Updates", width: "12rem", secondary: true, sort: row => row.status.settings.updates.schedule, cell: row => <span>{policyText(row.status, t)}
      {row.status.planned && <span className="muted-text"> · {t("planned {time}", { time: time.format(new Date(row.status.planned.at)) })}{row.status.planned.build ? ` → ${row.status.planned.build}` : ""}</span>}</span> },
    { id: "last", label: "Last change", width: "9rem", secondary: true, sort: row => row.status.history[0]?.at, cell: row => row.status.history[0]
      ? <Tag minimal intent={row.status.history[0].result === "updated" ? "success" : "danger"}>{t(row.status.history[0].result === "updated" ? "Updated" : row.status.history[0].result === "rolledBack" ? "Rolled back" : "Failed")}</Tag>
      : <span className="muted-text">—</span> },
  ];
  // In the order the table shows them: Update now updates them one after the other in it.
  const sortColumn = columns.find(column => column.id === view.sort?.column);
  const picked = sortRows(checkedRows, sortColumn?.sort ?? null, view.sort?.direction ?? "asc", locale);
  const ids = picked.map(row => row.entry.machine.id);
  return <section className="fleet-table-section" aria-label="llama.cpp">
    <FilterBar label={t("Filter the machines")} query={view.query} onQuery={query => onView({ query })} facets={[]} values={{}} onValues={() => undefined}
      shown={shown.length} total={rows.length} />
    {picked.length > 0 && <SelectionBar count={picked.length} total={shown.filter(row => row.entry.machine.manages).length}
      onAll={() => onCheck(shown.filter(row => row.entry.machine.manages).map(row => row.key))} onClear={() => onCheck([])}>
      <Button size="small" icon={<AppIcon name="refresh" size={13} />} text={t("Update now…")} disabled={busy} onClick={() => onUpdate("now", ids)} />
      <Button size="small" icon={<AppIcon name="timer" size={13} />} text={t("Update at…")} disabled={busy} onClick={() => onUpdate("at", ids)} />
      <Button size="small" icon={<AppIcon name="settings" size={13} />} text={t("Policy…")} disabled={busy} onClick={() => onPolicy(ids)} />
      {picked.some(row => row.status.planned) && <Button size="small" variant="minimal" intent="danger" text={t("Cancel the planned updates")} disabled={busy}
        onClick={() => void (async () => { for (const row of picked.filter(item => item.status.planned)) await run(row.entry.machine.id, { action: "cancelPlanned" }); })()} />}
    </SelectionBar>}
    <DataTable label="llama.cpp" rows={shown} columns={columns} rowKey={row => row.key} sort={view.sort} onSort={sort => onView({ sort })}
      selected={view.selected} onSelect={selected => onView({ selected })} rowState={row => updateAvailable(row.status) ? "connecting" : row.status.builds.some(build => build.current) ? "online" : undefined}
      empty={<EmptyState rows={rows.length} text="No machine to show." />}
      details={row => <BuildDetails row={row} busy={busy} run={run} time={time} onPolicy={() => onPolicy([row.entry.machine.id])}
        onUpdateAt={() => onUpdate("at", [row.entry.machine.id])} />} />
  </section>;
}

/** What the update policy of a machine says. */
export function policyText(status: Pick<ModelHostStatus, "settings">, t: (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string): string {
  const policy = status.settings.updates;
  const channel = t(policy.channel === "stable" ? "Stable" : policy.channel === "every" ? "Every build" : "This build");
  if (policy.schedule === "manual") return `${t("When I click Update")} · ${channel}`;
  if (policy.schedule === "daily") return `${t("Every day at {time}", { time: policy.time })} · ${channel}`;
  return `${t("Every {day} at {time}", { day: policy.day, time: policy.time })} · ${channel}`;
}

const verdictLabels: Readonly<Record<string, MessageKey>> = {
  fits: "fits", noGpu: "no GPU for it", driverTooOld: "driver too old", libraryMissing: "a library is missing", systemTooOld: "system too old",
  unsupportedGpu: "GPU not supported", notInRelease: "not in this release",
};

function BuildDetails({ row, busy, run, time, onPolicy, onUpdateAt }: { row: BuildRow; busy: boolean; run: RunAction; time: Intl.DateTimeFormat; onPolicy: () => void; onUpdateAt: () => void }) {
  const { t, locale } = useShellLanguage();
  const { status, entry } = row;
  const machine = entry.machine.id;
  const manage = entry.machine.manages;
  const installed = status.builds.filter(build => build.source !== "own" || build.current);
  const libraries = status.hardware.libraries;
  const facts: [MessageKey, ReactNode][] = [
    ["System", `${status.hardware.os} ${status.hardware.architecture}${status.hardware.cpuName ? ` · ${status.hardware.cpuName}` : ""}`],
    ["Memory", formatBytes(status.hardware.memoryBytes ?? null, locale)],
    ["GPUs", status.hardware.gpus.length === 0 ? t("No GPU") : status.hardware.gpus.map(gpu => `${gpu.name}${gpu.architecture ? ` (${gpu.architecture})` : ""}${gpu.memoryBytes ? `, ${formatBytes(gpu.memoryBytes, locale)}` : ""}${gpu.integrated ? ` · ${t("integrated")}` : ""}`).join("; ")],
    ["Libraries", [libraries.cudaDriverVersion && `CUDA ${libraries.cudaDriverVersion}`, libraries.rocmVersion && `${status.hardware.os === "windows" ? "HIP SDK" : "ROCm"} ${libraries.rocmVersion}${libraries.rocmPath ? ` (${libraries.rocmPath})` : ""}`,
      libraries.vulkan && "Vulkan", libraries.glibc && `glibc ${libraries.glibc}`].filter(Boolean).join(" · ") || "—"],
  ];
  return <div className="model-servers-details">
    <dl>{facts.map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd>{value}</dd></div>)}</dl>
    <h4>{t("Builds for this machine")}</h4>
    <table className="model-servers-models-table"><tbody>{status.candidates.map(candidate => <tr key={candidate.backend}>
      <td><strong>{backendName(candidate.backend)}</strong></td>
      <td><Tag minimal intent={candidate.verdict === "fits" ? "success" : "none"}>{t(verdictLabels[candidate.verdict] ?? "Unknown")}</Tag></td>
      <td className="muted-text">{candidate.message}</td>
      <td className="model-servers-model-actions">{manage && candidate.verdict === "fits" && !status.builds.some(build => build.backend === candidate.backend && build.current)
        && <Button size="small" variant="minimal" text={t("Install")} disabled={busy} onClick={() => void run(machine, { action: "installBuilds", backends: [candidate.backend] }, "The install started.")} />}</td>
    </tr>)}</tbody></table>
    {installed.length > 0 && <><h4>{t("Installed")}</h4>
      <table className="model-servers-models-table"><tbody>{installed.map(build => <tr key={`${build.tag}/${build.backend}`}>
        <td><strong>{build.tag}</strong> · {backendName(build.backend)}{build.backendVersion ? ` ${build.backendVersion}` : ""}</td>
        <td>{build.current ? <Tag minimal intent="success">{t("in use")}</Tag> : build.previous ? <Tag minimal>{t("previous")}</Tag> : null}{build.bad && <Tag minimal intent="danger">{t("failed its test")}</Tag>}</td>
        <td className="muted-text">{build.devices.map(device => `${device.name}: ${device.description}`).join(" · ") || t("CPU only")}</td>
        <td className="numeric">{formatBytes(build.sizeBytes, locale)}</td>
        <td className="model-servers-model-actions">{manage && !build.current && build.previous && <Button size="small" variant="minimal" text={t("Back to it")} disabled={busy}
          onClick={() => void run(machine, { action: "useBuild", backend: build.backend, build: build.tag }, "The servers run the previous build again.")} />}</td>
      </tr>)}</tbody></table></>}
    {status.history.length > 0 && <><h4>{t("Updates")}</h4><ul className="model-servers-history">{status.history.slice(0, 5).map(record => <li key={`${record.at}/${record.backend}`}>
      <span className="muted-text">{time.format(new Date(record.at))}</span> {backendName(record.backend)}: {record.from ?? "—"} → {record.to}
      <Tag minimal intent={record.result === "updated" ? "success" : "danger"}>{t(record.result === "updated" ? "Updated" : record.result === "rolledBack" ? "Rolled back" : "Failed")}</Tag>
      {record.reason && <span className="muted-text"> {record.reason}</span>}</li>)}</ul></>}
    {status.tasks.filter(task => task.endedAt).slice(-2).map(task => <p key={task.id} className={`model-servers-reason${task.state === "failed" ? " is-failed" : ""}`}>
      <AppIcon name={task.state === "failed" ? "error" : "check"} size={14} />{task.title}: {task.message}</p>)}
    {manage && <div className="machine-details-actions">
      {status.builds.some(build => build.current)
        ? <Button size="small" intent={updateAvailable(status) ? "primary" : "none"} icon={<AppIcon name="refresh" size={14} />} text={t("Update now")} disabled={busy}
          onClick={() => void run(machine, { action: "updateNow" }, "The update started.")} />
        : <Button size="small" intent="primary" icon={<AppIcon name="download" size={14} />} text={t("Install llama.cpp")} disabled={busy}
          onClick={() => void run(machine, { action: "installBuilds", backends: [] }, "The install started.")} />}
      <Button size="small" icon={<AppIcon name="timer" size={14} />} text={t("Update at…")} disabled={busy} onClick={onUpdateAt} />
      <Button size="small" icon={<AppIcon name="settings" size={14} />} text={t("Policy…")} disabled={busy} onClick={onPolicy} />
      <Button size="small" variant="minimal" icon={<AppIcon name="search" size={14} />} text={t("Check for updates")} disabled={busy}
        onClick={() => void run(machine, { action: "checkLatest" })} />
      {status.planned && <Button size="small" variant="minimal" intent="danger" text={t("Cancel the planned update")} disabled={busy}
        onClick={() => void run(machine, { action: "cancelPlanned" }, "The planned update was cancelled.")} />}
    </div>}
  </div>;
}
