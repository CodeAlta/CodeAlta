import type { SessionRuntimeScopedResponse, SessionUsageRequest, SessionUsageResponse } from "#neoastra";
import { projectRuntimeObservation, type RuntimeTarget } from "./runtimeObservations";
import { validateUsage, usageMessage } from "./sessionUsage";
import { correlatedIdentity } from "./runtimeState";

export type InfoField = readonly [string, string];
export type InfoObservations = Readonly<{ runtime: readonly InfoField[]; usage: readonly InfoField[];
  identity?: Readonly<{ runtime: string; attachment: string | null }> }>;
export const unavailableInfoObservations = (reason: string): InfoObservations => ({ runtime: [["Observation", reason]], usage: [["Observation", reason]] });
const show = (value: string | number | null | undefined) => value == null ? "Unknown" : String(value);
type Options = { signal: AbortSignal; timeoutMilliseconds: number };
type RuntimeRead = (request: RuntimeTarget["request"], options: Options) => Promise<SessionRuntimeScopedResponse>;
type UsageRead = (request: SessionUsageRequest, options: Options) => Promise<SessionUsageResponse>;

// Two bounded unary reads, never history or provider preparation. No mutation-capability observation.
export async function readSessionInfoObservations(target: RuntimeTarget, readRuntime: RuntimeRead, readUsage: UsageRead,
  signal: AbortSignal, current: () => boolean): Promise<InfoObservations> {
  const valid = () => !signal.aborted && current();
  const refused = () => unavailableInfoObservations("Unavailable: identity, attachment or scope changed. Refresh explicitly.");
  const request = target.request;
  if (!valid()) return refused();
  const reply = await readRuntime(request, { signal, timeoutMilliseconds: 10000 });
  if (!valid() || reply.hostEpoch !== request.expectedHostEpoch || reply.sessionId !== request.sessionId || reply.scope !== request.scope ||
    reply.projectId !== request.projectId || reply.projectPath !== request.projectPath) return refused();
  const projected = projectRuntimeObservation(reply);
  if (reply.status !== "ok" || !reply.observation || !projected.runtime) return unavailableInfoObservations(projected.label);
  const state = reply.observation;
  if (!correlatedIdentity(state, request.sessionId)) return refused();
  const entry = state.entry;
  const identity = { runtime: projected.runtime, attachment: entry?.attachmentGeneration ?? null };
  if (entry && (![entry.providerId, entry.providerKey, entry.modelId, entry.reasoningEffort, entry.agentPromptId, entry.pendingAgentPromptId, entry.activeRunId]
    .every(value => value === null || typeof value === "string" && value.length <= 256) ||
    ![entry.isRetiring, entry.isTerminated, entry.queueDrainInProgress].every(value => typeof value === "boolean"))) return refused();
  const runtime: InfoField[] = [["Observation", projected.label], ["Observed at", new Date().toISOString()],
    ["Runtime / attachment", `${show(state.runtimeInstanceId)} / ${show(entry?.attachmentGeneration)}`],
    ["Transition / retiring / terminated / queue drain", `${show(state.coordinatorTransitionInProgress?.toString())} / ${show(entry?.isRetiring.toString())} / ${show(entry?.isTerminated.toString())} / ${show(entry?.queueDrainInProgress.toString())}`],
    ["Observed run ID", show(entry?.activeRunId)], ["Observed provider ID / key", `${show(entry?.providerId)} / ${show(entry?.providerKey)}`],
    ["Observed model / reasoning", `${show(entry?.modelId)} / ${show(entry?.reasoningEffort)}`],
    ["Current attachment agent prompt ID", show(entry?.agentPromptId)], ["Pending agent prompt ID (next Send)", show(entry?.pendingAgentPromptId)]];
  if (!entry || state.coordinatorTransitionInProgress || entry.isRetiring || entry.isTerminated)
    return { runtime, identity, usage: [["Observation", "Unavailable: no stable attached runtime for this separate usage read."]] };
  const usageTarget = { epoch: request.expectedHostEpoch, sessionId: request.sessionId, scope: request.scope as "project" | "global",
    projectId: request.projectId, expectedProjectPath: request.projectPath };
  let raw: SessionUsageResponse;
  try { raw = await readUsage({ expectedHostEpoch: usageTarget.epoch, sessionId: usageTarget.sessionId, scope: usageTarget.scope,
    projectId: usageTarget.projectId, expectedProjectPath: usageTarget.expectedProjectPath }, { signal, timeoutMilliseconds: 10000 }); }
  catch { return valid() ? { runtime, identity, usage: [["Observation", "Error: usage read failed; no observation established."]] } : refused(); }
  if (!valid()) return refused();
  const usage = validateUsage(usageTarget, raw);
  if (!usage || usage.runtimeInstanceId !== null && (usage.runtimeInstanceId !== state.runtimeInstanceId || usage.attachmentGeneration !== entry.attachmentGeneration) ||
    !["ok", "no_observation", "read_failed", "wire_limit"].includes(usage.status)) return refused();
  const observation = usage.observation;
  const fields: InfoField[] = [["Observation", usage.status === "ok" ? "Last observed event, not live occupancy or a cumulative total."
    : usage.status === "read_failed" || usage.status === "wire_limit" ? `Error: ${usage.status}; no usage observation established.` : usageMessage(usage.status)],
    ["Attachment / omitted usage callbacks", `${show(usage.attachmentGeneration)} / ${show(usage.omittedUsageEvents)}`]];
  if (observation) fields.push(["Source / reported scope / sequence", `${observation.source} / ${observation.scope} / ${observation.sequence}`],
    ["Source time / event time", `${show(observation.sourceUpdatedAt)} / ${show(observation.eventTimestamp)}`],
    ["Reported window tokens / limit / messages", `${show(observation.window?.currentTokens)} / ${show(observation.window?.tokenLimit)} / ${show(observation.window?.messageCount)}`],
    ["Last-operation input / output", `${show(observation.lastOperation?.inputTokens)} / ${show(observation.lastOperation?.outputTokens)}`],
    ["Cache read / write / reused input / reasoning", `${show(observation.lastOperation?.cacheReadTokens)} / ${show(observation.lastOperation?.cacheWriteTokens)} / ${show(observation.lastOperation?.cachedInputTokens)} / ${show(observation.lastOperation?.reasoningTokens)}`],
    ["Reported cost (currency unspecified) / duration (ms)", `${show(observation.lastOperation?.cost)} / ${show(observation.lastOperation?.durationMs)}`],
    ["Invalid values / omitted data", `${observation.hadInvalidValues ? "Yes" : "No"} / ${observation.hadOmittedData ? "Yes" : "No"}`]);
  return { runtime, identity, usage: fields };
}

export function boundedInfoCopy(text: string): string | null {
  return text.trim() && text.length <= 32768 ? text : null;
}
