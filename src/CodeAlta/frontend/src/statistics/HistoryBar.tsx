import { Button, Callout, PopoverNext, ProgressBar } from "@blueprintjs/core";
import { useEffect, useId, useRef } from "react";
import { AppIcon } from "../AppIcon";
import { showToast } from "../appToaster";
import { useText } from "./text";
import { sentenceCase } from "./format";
import { doneSummary, historyView, progressOf, readingSeconds, reachedDay, skippedToRetry, timeLeft } from "./history";
import { useStatistics } from "./runtime";
import type { StatisticsStatus } from "./types";

// What the canvas says about the reading of the history: the choice the first time, the bar while it reads, the pause, the
// stop, the sessions that could not be read, and the one notification when it is done. The words are short and calm.

/** The words for a time left: "about 40 seconds". */
function useDuration() {
  const { t } = useText();
  return (parts: ReturnType<typeof timeLeft>): string => {
    if (!parts) return "";
    switch (parts.unit) {
      case "second": return parts.count === 1 ? t("about 1 second") : t("about {count} seconds", { count: parts.count });
      case "minute": return parts.count === 1 ? t("about 1 minute") : t("about {count} minutes", { count: parts.count });
      case "hour": return parts.count === 1 ? t("about 1 hour") : t("about {count} hours", { count: parts.count });
    }
  };
}

/** The first time: how much of the history to read. Shown instead of the pages, so the canvas never opens on empty charts. */
export function FirstTimeCard({ status }: Readonly<{ status: StatisticsStatus }>) {
  const { t, locale } = useText();
  const { history, fmt } = useStatistics();
  const duration = useDuration();
  const known = status.sessionsTotal > 0;
  const seconds = readingSeconds(status.bytesTotal, status.bytesPerSecond);
  const takes = seconds < 60 ? t("less than a minute") : duration(timeLeft({ ...status, etaSeconds: seconds }));
  const since = status.oldestDateReached ? fmt.dayLong(`${String(status.oldestDateReached).slice(0, 4)}-${String(status.oldestDateReached).slice(4, 6)}-${String(status.oldestDateReached).slice(6, 8)}`) : null;
  const disabled = history.busy;
  const titleId = useId();
  return <section className="stats-first" aria-labelledby={titleId}>
    <div className="stats-first-mark" aria-hidden="true"><AppIcon name="usage" size={26} /></div>
    <h2 id={titleId}>{t("Statistics of your sessions")}</h2>
    <p>{known && since
      ? status.sessionsTotal === 1 ? t("1 session since {date} can be read to build your statistics. It takes {time} and runs in the background.", { date: since, time: takes })
        : t("{count} sessions since {date} can be read to build your statistics. It takes {time} and runs in the background.", { count: status.sessionsTotal.toLocaleString(locale), date: since, time: takes })
      : t("Your sessions can be read to build your statistics. It runs in the background.")}</p>
    <div className="stats-first-actions">
      <Button intent="primary" disabled={disabled} onClick={() => void history.choose({ kind: "all" })}>{t("Read all the history")}</Button>
      <Button disabled={disabled} onClick={() => void history.choose({ kind: "days", days: 90 })}>{t("Last 90 days")}</Button>
      <Button variant="minimal" disabled={disabled} onClick={() => void history.choose({ kind: "fromToday" })}>{t("Start from today")}</Button>
    </div>
    {history.error && <p className="stats-first-error" role="alert">{history.error}</p>}
  </section>;
}

/** The sessions that could not be read: how many, and in a popover which ones, why, and "Try again". */
function SkippedSessions({ status }: Readonly<{ status: StatisticsStatus }>) {
  const { t } = useText();
  const { history } = useStatistics();
  return <PopoverNext placement="bottom-start" content={<div className="stats-popover stats-skipped">
    <ul>{status.skipped.map(item => <li key={item.sessionId}><code>{item.sessionId.slice(0, 8)}</code><span>{item.reason}</span></li>)}</ul>
    <Button size="small" onClick={() => void history.resume()} disabled={history.busy}>{t("Try again")}</Button>
  </div>}>
    <Button size="small" variant="minimal" icon={<AppIcon name="warning" size={13} />}>{status.skippedCount === 1 ? t("1 session could not be read") : t("{count} sessions could not be read", { count: status.skippedCount })}</Button>
  </PopoverNext>;
}

/** The bar under the frame while the history is read, paused, stopped, skipped or failed; nothing when everything is read. */
export function HistoryBar() {
  const { t, locale } = useText();
  const { status, history, fmt, visible } = useStatistics();
  const duration = useDuration();
  const view = historyView(status);
  const previous = useRef<StatisticsStatus | null>(null);

  // The one notification: the reading that was going on is done.
  useEffect(() => {
    const before = previous.current;
    previous.current = status;
    if (!visible || !status || !before) return;
    if (before.state === "reading" && status.state === "done" && status.sessionsTotal > 0) {
      const summary = doneSummary(status);
      showToast({ intent: "success", icon: "tick-circle", timeout: 6000,
        message: summary.since
          ? summary.sessions === 1 ? t("Your statistics are ready: 1 session since {date}.", { date: fmt.dayLong(summary.since) })
            : t("Your statistics are ready: {count} sessions since {date}.", { count: summary.sessions.toLocaleString(locale), date: fmt.dayLong(summary.since) })
          : summary.sessions === 1 ? t("Your statistics are ready: 1 session.")
            : t("Your statistics are ready: {count} sessions.", { count: summary.sessions.toLocaleString(locale) }) }, "statistics-ready");
    }
  }, [status, visible]); // eslint-disable-line react-hooks/exhaustive-deps

  if (view === "none" || view === "choice") return null;
  // What a screen reader is told (`role="status"`) is the sentence of the state, in each view: never the whole bar, whose buttons would be
  // read with it, and not the numbers of the progress, which change at every step and which the progress bar already gives.
  if (!status || view === "starting") return <div className="stats-history" data-view="starting"><span role="status">{t("Preparing the statistics…")}</span></div>;
  const reached = reachedDay(status);
  const left = status.sessionsTotal - status.sessionsDone;
  const reason = status.reason;
  return <div className="stats-history" data-view={view}>
    {view === "reading" && <>
      <ProgressBar className="stats-progress" value={progressOf(status)} intent="primary" stripes={false} animate={false} aria-label={t("Reading the history")} />
      <div className="stats-history-line">
        <span>{reason === "facts-improved" ? <span role="status">{t("Statistics were improved in this version. The history is being read again; what you see stays until then.")}</span>
          : reason === "catch-up" ? <span role="status">{left === 1 ? t("Catching up with 1 session…") : t("Catching up with {count} sessions…", { count: left.toLocaleString(locale) })}</span>
          : <><span role="status">{reason === "extended" ? t("Reading more history") : t("Reading the history")}</span>: {t("{done} of {total} sessions", { done: status.sessionsDone.toLocaleString(locale), total: status.sessionsTotal.toLocaleString(locale) })}
            {reached && <>, {t("back to {date}", { date: fmt.day(reached) })}</>}.{timeLeft(status) && <> {sentenceCase(t("{time} left.", { time: duration(timeLeft(status)) }), locale)}</>}</>}</span>
        <Button size="small" icon={<AppIcon name="pause" size={13} />} disabled={history.busy} onClick={() => void history.pause()}>{t("Pause")}</Button>
      </div>
    </>}
    {view === "paused" && <div className="stats-history-line">
      <span role="status">{reached
        ? left === 1 ? t("History paused at {date}. 1 session left.", { date: fmt.day(reached) }) : t("History paused at {date}. {count} sessions left.", { date: fmt.day(reached), count: left.toLocaleString(locale) })
        : left === 1 ? t("History paused. 1 session left.") : t("History paused. {count} sessions left.", { count: left.toLocaleString(locale) })}</span>
      <span className="stats-history-buttons">
        <Button size="small" intent="primary" icon={<AppIcon name="play" size={13} />} disabled={history.busy} onClick={() => void history.resume()}>{t("Resume")}</Button>
        <Button size="small" variant="minimal" icon={<AppIcon name="stop" size={13} />} disabled={history.busy} onClick={() => void history.stopHere()}>{t("Stop here")}</Button>
      </span>
    </div>}
    {view === "stopped" && <div className="stats-history-line">
      <span role="status">{reached ? t("The charts start on {date}.", { date: fmt.dayLong(reached) }) : t("The charts start where the reading stopped.")}</span>
      {skippedToRetry(status) > 0 && <SkippedSessions status={status} />}
    </div>}
    {view === "skipped" && <div className="stats-history-line" role="status">
      <SkippedSessions status={status} />
    </div>}
    {view === "failed" && <Callout className="stats-failed" intent="danger" icon={<AppIcon name="error" size={16} />} title={t("The statistics could not start")}>
      <p role="status">{status.error ?? t("Something went wrong.")}</p><Button size="small" onClick={() => void history.resume()} disabled={history.busy}>{t("Try again")}</Button></Callout>}
    {history.error && <p className="stats-history-error" role="alert">{history.error}</p>}
  </div>;
}
