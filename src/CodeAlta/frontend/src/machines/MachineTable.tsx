import { useMemo, useState } from "react";
import { Button, PopoverNext, Tag } from "@blueprintjs/core";
import type { MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import { FilterBar, type FilterFacet } from "../fleet/console/FilterBar";
import { facetCounts, filterRows, type Facet, type FacetValues, type TableSort } from "../fleet/console/tableRows";
import { machineIntent, machineState, machineStateLabel, pairingWords, throughServer, type MachineState } from "./machines";
import { managesMachine } from "./machineManagement";

/** How this computer reaches a machine: paired with it, and over a direct or a relayed channel; or through its server. */
export type MachineReach = "direct" | "relayed" | "paired" | "server";

/** How this computer reaches a machine, for its column and its filter. */
export function machineReach(machine: Pick<MachineView, "paired" | "channel">): MachineReach {
  if (throughServer(machine)) return "server";
  return machine.channel === "direct" ? "direct" : machine.channel === "relayed" ? "relayed" : "paired";
}

const reachLabels: Readonly<Record<MachineReach, MessageKey>> = {
  direct: "Paired, direct", relayed: "Paired, relayed", paired: "Paired", server: "Through your server",
};

const stateRank: Readonly<Record<MachineState, number>> = { online: 0, connecting: 1, offline: 2, unauthorized: 3 };

/** The system of a machine as its filter names it: the first word of what it said (Windows, macOS, Linux). */
export function machineSystem(machine: Pick<MachineView, "os">): string {
  return machine.os?.trim().split(/[\s(]/u)[0] || "";
}

/**
 * The machines of this computer as a table that scales: filtered by state, system and reach, sorted by any column, and
 * a row opens what else is known of its machine in place, with its page and Forget.
 */
export function MachineTable({ machines, busy, onOpen, onForget }: {
  machines: readonly MachineView[]; busy: boolean; onOpen: (id: string) => void; onForget: (machine: MachineView) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [query, setQuery] = useState("");
  const [values, setValues] = useState<FacetValues>({});
  const [sort, setSort] = useState<TableSort | null>({ column: "state", direction: "asc" });
  const [selected, setSelected] = useState<string | null>(null);
  const time = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }), [locale]);
  const facets = useMemo<Facet<MachineView>[]>(() => [
    { id: "state", values: machine => [machineState(machine)] },
    { id: "system", values: machine => machineSystem(machine) ? [machineSystem(machine)] : [] },
    { id: "reach", values: machine => [machineReach(machine)] },
    { id: "access", values: machine => managesMachine(machine) ? ["manage"] : ["use"] },
  ], []);
  const shown = useMemo(() => filterRows(machines, facets, values, query,
    machine => [machine.name, machine.os, machine.version, machine.address, machine.fingerprint, machine.problem].filter(Boolean).join(" ")), [machines, facets, values, query]);
  const options = (id: string, label: (value: string) => string): FilterFacet["options"] =>
    [...facetCounts(machines, facets.find(facet => facet.id === id)!)].map(([value, count]) => ({ value, label: label(value), count }));
  const filters: FilterFacet[] = [
    { id: "state", label: "State", icon: "issueOpen", options: options("state", value => t(machineStateLabel(value as MachineState))) },
    { id: "system", label: "System", icon: "computer", options: options("system", value => value) },
    { id: "reach", label: "Reached", icon: "remote", options: options("reach", value => t(reachLabels[value as MachineReach])) },
    { id: "access", label: "Access", icon: "shield", options: options("access", value => t(value === "manage" ? "Managed from here" : "Sessions only")) },
  ];
  const columns: DataColumn<MachineView>[] = [
    { id: "name", label: "Machine", sort: machine => machine.name, cell: machine => <span className="machine-cell-name">
      <AppIcon name="remote" size={15} /><strong>{machine.name}</strong>
      {managesMachine(machine) && <AppIcon name="shield" size={12} className="machine-cell-managed" aria-label={t("Managed from here")} />}
    </span> },
    { id: "state", label: "State", width: "8.5rem", sort: machine => stateRank[machineState(machine)], cell: machine => {
      const state = machineState(machine);
      return <Tag minimal round intent={machineIntent(state)}>{t(machineStateLabel(state))}</Tag>;
    } },
    { id: "system", label: "System", width: "9rem", secondary: true, sort: machine => machine.os, cell: machine => machine.os ?? <span className="muted-text">—</span> },
    { id: "version", label: "Version", width: "6.5rem", secondary: true, sort: machine => machine.version, cell: machine => machine.version ?? <span className="muted-text">—</span> },
    { id: "sessions", label: "Sessions", width: "6.5rem", align: "end", sort: machine => machine.sessions, cell: machine => machine.sessions },
    { id: "reach", label: "Reached", width: "11rem", secondary: true, sort: machine => t(reachLabels[machineReach(machine)]), cell: machine => {
      const reach = machineReach(machine);
      return <span className="with-logo"><AppIcon name={reach === "server" ? "server" : "remote"} size={13} />{t(reachLabels[reach])}</span>;
    } },
    { id: "seen", label: "Last seen", width: "10rem", secondary: true, sort: machine => machine.lastSeen ? Date.parse(machine.lastSeen) : null,
      cell: machine => machine.lastSeen ? <span title={machine.lastSeen}>{time.format(new Date(machine.lastSeen))}</span> : <span className="muted-text">—</span> },
  ];
  return <section className="fleet-table-section" aria-label={t("Machines")}>
    <FilterBar label={t("Filter the machines")} query={query} onQuery={setQuery} facets={filters} values={values} onValues={setValues} shown={shown.length} total={machines.length} />
    <DataTable label={t("Machines")} rows={shown} columns={columns} rowKey={machine => machine.id} sort={sort} onSort={setSort} selected={selected} onSelect={setSelected}
      rowState={machine => machineState(machine)}
      empty={<span className="muted-text">{t("No machine matches these filters.")}</span>}
      details={machine => <MachineDetails machine={machine} busy={busy} time={time} onOpen={() => onOpen(machine.id)} onForget={() => onForget(machine)} />} />
  </section>;
}

/** What else is known of a machine, under its row: its key, its address, why it does not answer; its page, and Forget. */
export function MachineDetails({ machine, busy, time, onOpen, onForget }: { machine: MachineView; busy: boolean; time: Intl.DateTimeFormat; onOpen: () => void; onForget: () => void }) {
  const { t } = useShellLanguage();
  const state = machineState(machine);
  const facts: [MessageKey, React.ReactNode][] = [
    ["System", machine.os ?? "—"],
    ["Version", machine.version ? `CodeAlta ${machine.version}` : "—"],
    ["Address", machine.address ?? "—"],
    ["Reached", t(reachLabels[machineReach(machine)])],
    ["Key", <span className="machine-details-key" title={machine.words}>{pairingWords(machine.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</span>],
    ["Paired", machine.paired ? time.format(new Date(machine.pairedAt)) : t("Through your server")],
  ];
  return <div className="machine-details">
    {machine.problem && state !== "online" && <p className="machine-details-problem"><AppIcon name="warning" size={14} />{machine.problem}</p>}
    <dl>{facts.map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd>{value}</dd></div>)}</dl>
    <div className="machine-details-actions">
      <Button intent="primary" size="small" icon={<AppIcon name="openExternal" size={14} />} text={t("Open its page")} onClick={onOpen} />
      {/* A machine of the server is not paired here: nothing to forget; leaving the server, or removing it there, takes it away. */}
      {!throughServer(machine) && <PopoverNext placement="top" content={<div className="machine-forget">
        <p>{t("Forget {name}?", { name: machine.name })}</p>
        <p className="muted-text">{t("Its sessions are no longer shown here; they go on on the machine.")}</p>
        <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={onForget}>{t("Forget")}</Button></div>}>
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={busy} text={t("Forget")} />
      </PopoverNext>}
    </div>
  </div>;
}
