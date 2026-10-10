import { useMemo } from "react";
import { WeekdayHourHeatmap, histogram, histogramOption } from "../../charts";
import { Block } from "../blocks";
import { usePageColors } from "../colors";
import { StatChart } from "../StatChart";
import { DistributionChart, SeriesChart } from "../pageKit";
import { useDistribution, useSeries, useSessions, useWeekHour } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import { combineSeries, firstQuery } from "./shared";

// Activity: when do I work with it, and how long do runs take?

/** The Activity page. */
export function ActivityPage() {
  const { t } = useText();
  const { fmt, weekStart } = useStatistics();
  const colors = usePageColors();
  const started = useSeries("sessions-started", null, { main: true });
  const active = useSeries("sessions-active", null);
  const completed = useSeries("runs-completed", null);
  const failed = useSeries("runs-failed", null);
  const interrupted = useSeries("runs-interrupted", null);
  const time = useSeries("active-time", null);
  const atOnce = useSeries("sessions-at-once", null);
  const week = useWeekHour();
  const duration = useDistribution("run-duration", null);
  const sessions = useSessions("runs", { extra: { limit: 500, comparison: "none" } });

  const sessionsPair = useMemo(() => started.data && active.data ? combineSeries([{ result: started.data, key: "started", label: t("Started") }, { result: active.data, key: "active", label: t("Active") }], "sessions") : null,
    [started.data, active.data, t]);
  const outcomes = useMemo(() => completed.data && failed.data && interrupted.data ? combineSeries([
    { result: completed.data, key: "completed", label: t("Completed") }, { result: failed.data, key: "failed", label: t("Failed") }, { result: interrupted.data, key: "interrupted", label: t("Interrupted") }], "runs") : null,
  [completed.data, failed.data, interrupted.data, t]);
  const outcomeColor = (line: { key: string }) => line.key === "completed" ? colors.good : line.key === "failed" ? colors.bad : colors.warn;
  const pairColor = (line: { key: string }) => line.key === "started" ? colors.at(0) : colors.at(4);
  const matrix = useMemo(() => week.data ? week.data.activeMs.map(row => [...row]) : [], [week.data]);

  const runsPerSession = useMemo(() => sessions.data ? histogramOption(histogram(sessions.data.rows.map(row => row.runs), { bins: 14, scale: "log" }), { name: t("Sessions"), label: (from, to) => `${Math.round(from)}–${Math.round(to)}` }) : null, [sessions.data, t]);
  const timePerSession = useMemo(() => sessions.data ? histogramOption(histogram(sessions.data.rows.map(row => row.activeMs), { bins: 14, scale: "log", min: 1000 }),
    { name: t("Sessions"), label: (from, to) => `${fmt.duration(from)}–${fmt.duration(to)}` }) : null, [sessions.data, t, fmt]);

  return <div className="stats-grid">
    <Block title={t("Sessions started and active")} span={6} minHeight={240} query={firstQuery(started, active)} empty={sessionsPair !== null && sessionsPair.series.every(line => line.total === 0)}>
      {sessionsPair && <SeriesChart result={sessionsPair} stacked={false} ariaLabel={t("Sessions started and active")} colorOf={pairColor} />}
    </Block>
    <Block title={t("Runs by outcome")} span={6} minHeight={240} query={firstQuery(completed, failed, interrupted)} empty={outcomes !== null && outcomes.series.every(line => line.total === 0)}>
      {outcomes && <SeriesChart result={outcomes} ariaLabel={t("Runs by outcome")} colorOf={outcomeColor} />}
    </Block>
    <Block title={t("Active time")} span={6} minHeight={240} query={time} empty={time.data !== undefined && time.data.series.every(line => line.total === 0)}>
      {time.data && <SeriesChart result={time.data} kind="area" ariaLabel={t("Active time")} colorOf={() => colors.at(0)} />}
    </Block>
    <Block title={t("When in the week")} span={6} minHeight={240} query={week} empty={week.data !== undefined && matrix.every(row => row.every(value => value === 0))}>
      {week.data && <div className="stats-calendar"><WeekdayHourHeatmap matrix={matrix} weekStart={weekStart} ariaLabel={t("Active time by day of the week and hour")}
        describe={(row, hour, value) => `${week.data!.weekdays[row] ?? ""} ${String(hour).padStart(2, "0")}:00: ${value ? fmt.duration(value) : t("no activity")}`}
 /></div>}
    </Block>
    <Block title={t("Sessions at once")} caption={atOnce.data?.query.notes.includes("runs-of-unknown-time-left-out") ? t("Runs with unknown timing are left out.") : undefined}
      span={12} minHeight={220} query={atOnce} empty={atOnce.data !== undefined && atOnce.data.series.every(line => line.total === 0)}>
      {atOnce.data && <SeriesChart result={atOnce.data} kind="line" stacked={false} height={200} ariaLabel={t("Most sessions running at the same time")} name={() => t("Sessions at once")} colorOf={() => colors.at(4)} />}
    </Block>
    <Block title={t("How long a run takes")} span={12} minHeight={260} query={duration} empty={duration.data !== undefined && duration.data.count === 0}>
      {duration.data && <DistributionChart result={duration.data} name={t("Runs")} ariaLabel={t("How long a run takes, on a logarithmic scale")} height={240} />}
    </Block>
    <Block title={t("Runs per session")} span={6} minHeight={220} query={sessions} empty={sessions.data !== undefined && sessions.data.rows.length === 0}>
      {runsPerSession && sessions.data && <StatChart option={runsPerSession} ariaLabel={t("Number of sessions by number of runs")} height={200} group="activity-sessions" />}
    </Block>
    <Block title={t("Active time per session")} span={6} minHeight={220} query={sessions} empty={sessions.data !== undefined && sessions.data.rows.length === 0}>
      {timePerSession && sessions.data && <StatChart option={timePerSession} ariaLabel={t("Number of sessions by active time")} height={200} group="activity-sessions-time" />}
    </Block>
  </div>;
}
