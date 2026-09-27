import type { SessionInfoView } from "./sessionInfo";
import { boundedInfoCopy, type InfoObservations } from "./sessionInfoObservations";
import type { MessageKey } from "./localization";

export const infoDescription = "Saved metadata and separate point-in-time observations. Reads are not atomic; may already be stale. Not liveness, finality or command permission. Unknown is not zero.";
export const infoDemoDescription = "Demo snapshot; values are local to this preview.";
export const infoUsageDescription = "One admitted event, not history totals or model/catalog context capacity. Prompt IDs are attachment observations, not local next-Send overrides or prompt source text.";

// Only controller-authored field *labels*, never field values or free-form outcomes.
const fieldLabels: readonly MessageKey[] = Object.freeze([
  "Observation", "Observed at", "Runtime / attachment", "Transition / retiring / terminated / queue drain",
  "Observed run ID", "Observed provider ID / key", "Observed model / reasoning", "Current attachment agent prompt ID",
  "Pending agent prompt ID (next Send)", "Attachment / omitted usage callbacks", "Source / reported scope / sequence",
  "Source time / event time", "Reported window tokens / limit / messages", "Last-operation input / output",
  "Cache read / write / reused input / reasoning", "Reported cost (currency unspecified) / duration (ms)", "Invalid values / omitted data",
]);
export function infoFieldLabel(label: string): MessageKey | null {
  return fieldLabels.find(key => key === label) ?? null;
}

// Keep the existing English/plain-data clipboard contract independent of display locale.
// Separators match the original English data-info-copy blocks (heading, dl and paragraph innerText).
export function canonicalInfoCopy(info: SessionInfoView, demo: boolean, observations: InfoObservations): string | null {
  const saved = [
    ["Session ID", info.id || "Not recorded"],
    ["Title", info.title + (info.titleTruncated ? "\nTitle shortened in the bounded snapshot." : "")],
    ["Scope", info.scope + (info.scopeWarning ? `\n${info.scopeWarning}` : "")],
    ["Recorded working directory", info.path ?? "Not recorded or unverified"],
    ["Provider", info.provider ?? "Not recorded or unverified"],
    ["Saved update", info.updatedAt || "Not recorded or unverified"],
    ["Recorded creation time", info.createdAt || "Not recorded or unavailable"],
  ];
  const fields = (rows: readonly (readonly string[])[]) => rows.map(row => row.join("\n")).join("\n");
  return boundedInfoCopy([
    demo ? infoDemoDescription : infoDescription,
    `Saved metadata\n${fields(saved)}`,
    `Observed runtime configuration\n${fields(observations.runtime)}`,
    `Last-observed usage\n\n${infoUsageDescription}\n\n${fields(observations.usage)}`,
  ].join("\n\n"));
}
