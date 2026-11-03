import { useEffect, useState } from "react";
import type { FleetStatisticsMachineView, FleetStatisticsView } from "#neoastra";
import { StatTile } from "../charts";
import { Block, Choice, DataTable, type Column } from "./blocks";
import { fleetDays, fleetPeriods, fleetProblem, fleetRows, costOf, localTimeZone, tokensOf, type FleetDimension, type FleetPeriod, type FleetRow,
  type FleetStatisticsApi } from "./fleet";
import { useStatistics } from "./runtime";
import { useText } from "./text";

// "My fleet": the numbers of every machine of the person, as CodeAlta Server sums what they published. Three questions for a period
// (by machine, by project, by model); the tiles are the totals of the first.

type Answer = Readonly<{ view: FleetStatisticsView | null; problem: string | null; loading: boolean }>;

const dimensions = { machine: ["machine"], project: ["project"], model: ["provider", "model"] } as const satisfies Record<string, readonly FleetDimension[]>;

function useFleetAnswer(api: FleetStatisticsApi, period: FleetPeriod, groupBy: readonly FleetDimension[], visible: boolean, attempt: number): Answer {
  const { t } = useText();
  const [answer, setAnswer] = useState<Answer>({ view: null, problem: null, loading: true });
  const key = groupBy.join(",");
  useEffect(() => {
    if (!visible) return;
    const controller = new AbortController();
    setAnswer(previous => ({ ...previous, loading: true }));
    const days = fleetDays(period, new Date());
    api.statistics({ ...days, timeZone: localTimeZone(), groupBy }, controller.signal).then(
      result => { if (!controller.signal.aborted) setAnswer({ view: result.view, problem: fleetProblem(t, result.status), loading: false }); },
      error => { if (!controller.signal.aborted) setAnswer({ view: null, problem: error instanceof Error ? error.message : String(error), loading: false }); });
    return () => controller.abort();
    // The dimensions are compared by their text: a new array of the same dimensions asks nothing again.
  }, [api, period, key, visible, attempt, t]); // eslint-disable-line react-hooks/exhaustive-deps
  return answer;
}

/** The "My fleet" scope of the Statistics canvas, over the server's sums. */
export function FleetScope({ api }: Readonly<{ api: FleetStatisticsApi }>) {
  const { t } = useText();
  const { fmt, visible } = useStatistics();
  const [period, setPeriod] = useState<FleetPeriod>("30d");
  const [attempt, setAttempt] = useState(0);
  const byMachine = useFleetAnswer(api, period, dimensions.machine, visible, attempt);
  const byProject = useFleetAnswer(api, period, dimensions.project, visible, attempt);
  const byModel = useFleetAnswer(api, period, dimensions.model, visible, attempt);
  const totals = byMachine.view?.totals;
  const query = (answer: Answer) => ({ loading: answer.loading && !answer.view, refreshing: answer.loading && !!answer.view, placeholder: false,
    error: answer.problem ? new Error(answer.problem) : null, retry: () => setAttempt(value => value + 1) });
  const periodLabel: Record<FleetPeriod, string> = { "7d": t("Last 7 days"), "30d": t("Last 30 days"), "90d": t("Last 90 days"), month: t("This month") };
  const machines = new Map<string, FleetStatisticsMachineView>((byMachine.view?.machines ?? []).map(machine => [machine.id, machine]));
  const usd = (byMachine.view?.groups ?? []).some(group => costOf(group.measures) > 0);
  const common = (first: Column<FleetRow>): Column<FleetRow>[] => [
    first,
    { id: "tokens", header: t("Tokens"), sort: row => row.tokens, align: "end", render: row => fmt.compact(row.tokens) },
    { id: "time", header: t("Active time"), sort: row => row.activeMs, align: "end", render: row => fmt.duration(row.activeMs) },
    { id: "runs", header: t("Runs"), sort: row => row.runs, align: "end", render: row => fmt.number(row.runs) },
    { id: "calls", header: t("Tool calls"), sort: row => row.toolCalls, align: "end", render: row => fmt.number(row.toolCalls) },
    ...(usd ? [{ id: "cost", header: t("Cost (USD)"), sort: (row: FleetRow) => row.usd, align: "end" as const, render: (row: FleetRow) => row.usd ? fmt.cost("usd", row.usd) : "–" }] : []),
  ];
  const machineRows = byMachine.view ? fleetRows(t, byMachine.view, "machine") : [];
  // Every machine of the person shows, the ones that published nothing in the period too: a gap shows rather than hides.
  for (const machine of machines.values()) {
    if (!machineRows.some(row => row.key === machine.id)) machineRows.push({ key: machine.id, label: machine.name, detail: null, tokens: 0, inputTokens: 0, outputTokens: 0, activeMs: 0, runs: 0, toolCalls: 0, usd: 0 });
  }

  const machineColumns: Column<FleetRow>[] = [
    ...common({ id: "name", header: t("Machine"), sort: row => row.label, wide: true, render: row => <span className="stats-fleet-name">{row.label}
      {machines.get(row.key)?.isThisComputer && <span className="stats-fleet-detail">{t("This machine")}</span>}</span> }),
    { id: "published", header: t("Last published"), sort: row => machines.get(row.key)?.lastPublishedAt ?? "", align: "end",
      render: row => machines.get(row.key)?.lastPublishedAt ? fmt.dateTime(machines.get(row.key)!.lastPublishedAt!) : t("Not published yet") },
  ];
  const projectColumns = common({ id: "name", header: t("Project"), sort: row => row.label, wide: true, render: row => row.label });
  const modelColumns = common({ id: "name", header: t("Model"), sort: row => row.label, wide: true, render: row => <span className="stats-fleet-name">{row.label}
    {row.detail && <span className="stats-fleet-detail">{row.detail}</span>}</span> });
  return <div className="stats-page stats-fleet" data-page="fleet">
    <div className="stats-fleet-bar">
      <Choice label={t("Period")} value={period} onChange={setPeriod} options={fleetPeriods.map(value => ({ value, label: periodLabel[value] }))} />
    </div>
    {totals && <div className="stats-tiles">
      <StatTile label={t("Tokens")} value={fmt.compact(tokensOf(totals))} />
      <StatTile label={t("Active time")} value={fmt.duration(totals.activeMs)} />
      <StatTile label={t("Runs")} value={fmt.number(totals.runs)} />
      <StatTile label={t("Tool calls")} value={fmt.number(totals.toolCalls)} />
      {costOf(totals) > 0 && <StatTile label={t("Cost")} value={fmt.costShort("usd", costOf(totals))} />}
    </div>}
    <div className="stats-grid">
      <Block title={t("By machine")} span={12} minHeight={80} query={query(byMachine)} empty={machineRows.length === 0}>
        <DataTable label={t("By machine")} columns={machineColumns} rows={machineRows} rowKey={row => row.key} />
      </Block>
      <Block title={t("By project")} span={6} minHeight={80} query={query(byProject)} empty={(byProject.view?.groups.length ?? 0) === 0}>
        <DataTable label={t("By project")} columns={projectColumns} rows={byProject.view ? fleetRows(t, byProject.view, "project") : []} rowKey={row => row.key} />
      </Block>
      <Block title={t("By model")} span={6} minHeight={80} query={query(byModel)} empty={(byModel.view?.groups.length ?? 0) === 0}>
        <DataTable label={t("By model")} columns={modelColumns} rows={byModel.view ? fleetRows(t, byModel.view, "model") : []} rowKey={row => row.key} />
      </Block>
    </div>
  </div>;
}
