import { useMemo } from "react";
import { StatTile } from "../../charts";
import { Block, RankedBars, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { senderLabel } from "../labels";
import { SeriesChart, useDrill } from "../pageKit";
import { useDetails, useSeries, useSessions } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { SeriesLine } from "../types";
import { depthLabel, firstQuery } from "./shared";

// Agents: how much work is delegated — sub-agents, automations, reminders?

const subAgent = "sub-agent";

/** The Agents page. */
export function AgentsPage() {
  const { t } = useText();
  const { fmt } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const created = useSeries("sessions-started", "delegated", { main: true });
  const tokens = useSeries("tokens", "delegated", { extra: { comparison: "none" } });
  const time = useSeries("active-time", "delegated", { extra: { comparison: "none" } });
  const sessions = useSessions("runs", { extra: { limit: 500, comparison: "none" } });
  const origins = useDetails("run-origin", { extra: { comparison: "none" } });
  const runsByOrigin = useSeries("runs", "origin");
  const depth = useDetails("sub-agent-depth", { extra: { comparison: "none" } });
  const share = (result: typeof tokens.data) => {
    if (!result) return null;
    const all = result.series.reduce((sum, line) => sum + line.total, 0);
    const sub = result.series.find(line => line.key === subAgent)?.total ?? 0;
    return all > 0 ? { share: sub / all, sub, all } : null;
  };
  const tokenShare = share(tokens.data), timeShare = share(time.data);
  const largest = useMemo<RankedItem[]>(() => (sessions.data?.rows ?? []).filter(row => row.subAgents > 0).sort((a, b) => b.subAgents - a.subAgents).slice(0, 6)
    .map(row => ({ key: row.sessionId, label: row.title ?? row.sessionId.slice(0, 8), detail: row.projectName, value: row.subAgents, text: t("{count} sub-agents", { count: row.subAgents }), share: 0 })), [sessions.data, t]);
  const name = (line: SeriesLine) => line.key === subAgent ? t("Sub-agents") : t("Your sessions");
  const depthItems = (depth.data?.rows ?? []).map(row => ({ key: row.name, label: depthLabel(t, Number(row.name)), value: row.count, text: `${fmt.number(row.count)} · ${fmt.percent(row.share, 0)}`, share: row.share }));
  const originItems = (origins.data?.rows ?? []).map(row => ({ key: row.name, label: senderLabel(t, row.name), value: row.count, text: `${fmt.number(row.count)} · ${fmt.percent(row.share, 0)}`, share: row.share }));
  return <div className="stats-grid">
    <Block title={t("Sessions started")} span={12} minHeight={260} query={created} empty={created.data !== undefined && created.data.series.every(line => line.total === 0)}>
      {created.data && <SeriesChart result={created.data} height={240} ariaLabel={t("Sessions started, yours and sub-agents")} name={name}
        colorOf={line => line.key === subAgent ? colors.at(4) : colors.at(0)} />}
    </Block>
    <Block title={t("Delegated")} span={6} minHeight={120} query={firstQuery(tokens, time)} empty={tokenShare === null && timeShare === null}>
      <div className="stats-tiles stats-tiles-inner">
        {tokenShare && <StatTile label={t("Share of tokens in sub-agents")} value={fmt.percent(tokenShare.share)} />}
        {timeShare && <StatTile label={t("Share of time in sub-agents")} value={fmt.percent(timeShare.share)} />}
      </div>
    </Block>
    <Block title={t("Depth of the sub-agents")} span={6} minHeight={120} query={depth} empty={depthItems.length === 0}>
      <RankedBars label={t("Sub-agent sessions by depth")} items={depthItems} color={colors.at(4)} />
    </Block>
    <Block title={t("The largest trees of sessions")} span={12} minHeight={120} query={sessions} empty={largest.length === 0}>
      <RankedBars label={t("Sessions with the most sub-agents")} items={largest} color={colors.at(4)} onSelect={item => drill.openSession(item.key)} selectLabel={item => t("Open {title}", { title: item.label })} />
    </Block>
    <Block title={t("Runs started by")} span={6} minHeight={240} query={runsByOrigin} empty={runsByOrigin.data !== undefined && runsByOrigin.data.series.every(line => line.total === 0)}>
      {runsByOrigin.data && <SeriesChart result={runsByOrigin.data} height={220} ariaLabel={t("Runs by who started them")} name={line => senderLabel(t, line.key)} />}
    </Block>
    <Block title={t("Automations and reminders")} span={6} minHeight={240} query={origins} empty={originItems.length === 0}>
      <RankedBars label={t("Runs by who started them")} items={originItems} color={colors.at(5)} />
    </Block>
  </div>;
}
