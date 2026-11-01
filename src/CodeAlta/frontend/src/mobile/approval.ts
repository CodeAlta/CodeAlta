import type { ApprovalMark, ApprovalRequest, Live } from "./contract";

// What sessions ask permission for, without the DOM: what the list says of them, and what a card says of a request.

/** Whether a session waits for the user to allow or deny a request; a request waits while its turn runs. */
export const approvalWaiting = (live: Live | null | undefined): boolean => !!live?.approving;

/** What tells the page to read the requests of a session again: the first one, or their number, changed. */
export const approvalMarkKey = (mark: ApprovalMark | null | undefined): string => mark ? `${mark.id}\n${mark.count}` : "";

/** The question of a card. */
export function approvalQuestion(request: ApprovalRequest): string {
  return request.kind === "commandExecution" ? "Allow this command?"
    : request.kind === "skill" ? "Load this skill?"
    : "Allow file changes under this folder?";
}

/** What a request is about, as the card shows it first, in a fixed-width face. */
export function approvalSubject(request: ApprovalRequest): string {
  if (request.kind === "commandExecution") return request.command ?? "";
  if (request.kind === "skill") return request.skill ? `${request.skill.name} (${request.skill.server})` : "";
  return request.grantRoot ?? "";
}

/** What tells a card to draw itself again, so that what the user reads does not move while nothing changed. */
export const approvalKey = (request: ApprovalRequest, hasMore: boolean, sending: boolean, armed: boolean, note: string): string =>
  JSON.stringify([request.id, request.kind, approvalSubject(request), request.workingDirectory, request.reason, request.shortened, request.outsideSandbox, hasMore, sending, armed, note]);
