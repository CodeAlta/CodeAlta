import { memo } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptCall } from "../timeline";
import { callDuration, formatDuration, toolState } from "../toolCall";

/** The most calls of a script its card lists; the others are counted. */
export const shownScriptCalls = 6;

/** How the calls of a script went: how many, and how many ended each way. */
export function scriptCallCounts(calls: ReadonlyArray<ScriptCall>) {
  let running = 0, failed = 0;
  for (const call of calls) {
    const state = toolState(call.phase ?? undefined);
    if (state === "failed") failed++;
    else if (state === "running" || state === "pending") running++;
  }
  return { total: calls.length, running, failed };
}

/**
 * The calls the script of a call made (Code Mode's `exec`), under its tile: their counts, then the last ones, each with
 * its state, its tool, its step and its duration. The running calls and the failed ones come first among those shown.
 */
export const ScriptCalls = memo(function ScriptCalls({ calls }: { calls: ReadonlyArray<ScriptCall> }) {
  const { t } = useShellLanguage();
  const counts = scriptCallCounts(calls);
  const notable = calls.filter(call => { const state = toolState(call.phase ?? undefined); return state !== "completed" && state !== "canceled"; });
  const rest = calls.filter(call => !notable.includes(call));
  const shown = [...notable.slice(-shownScriptCalls), ...rest.slice(-Math.max(0, shownScriptCalls - notable.length))]
    .sort((a, b) => calls.indexOf(a) - calls.indexOf(b));
  return <div className="script-calls" role="group" aria-label={t("Calls of the script")}>
    <div className="script-calls-counts">
      <span>{t("{count} call(s)", { count: counts.total })}</span>
      {counts.running > 0 && <span className="script-calls-running"><ActivitySpinner size={9} />{t("{count} running", { count: counts.running })}</span>}
      {counts.failed > 0 && <span className="script-calls-failed">{t("{count} failed", { count: counts.failed })}</span>}
      {calls.length > shown.length && <span>{t("{count} more", { count: calls.length - shown.length })}</span>}
    </div>
    <ol className="script-calls-list">
      {shown.map(call => {
        const state = toolState(call.phase ?? undefined);
        const duration = callDuration(call.startedAt, call.endedAt);
        return <li key={call.activityId} data-state={state} title={call.error ?? undefined}>
          {state === "running" || state === "pending" ? <ActivitySpinner size={9} className="script-call-spinner" /> : <span className="script-call-dot" aria-hidden="true" />}
          <strong>{call.name}</strong>
          {call.stepId && <span className="script-call-step">{call.stepId}</span>}
          {call.error ? <span className="script-call-error">{call.error}</span> : <span />}
          {duration !== null && <span className="script-call-duration">{formatDuration(duration)}</span>}
        </li>;
      })}
    </ol>
  </div>;
});
