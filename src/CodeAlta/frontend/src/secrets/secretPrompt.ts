import type { MessageKey } from "../localization";

/** The event of the window the shell's `secrets-pending` notice raises: the uses that wait for the user changed. */
export const secretsPendingEvent = "codealta:secrets-pending";

/** A use of a secret that asks before use and waits for the user, as the host tells the prompt of the window. */
export type PromptedUse = Readonly<{
  name: string;
  use?: string | null;
  at: string;
  /** The session whose shell asks, for the shell of a session. */
  sessionId?: string | null;
  /** Until when something waits for the answer (ISO 8601): the shell of a session that starts after it. */
  awaitedUntil?: string | null;
  /** Whether the window asks at once. */
  prompt: boolean;
}>;

/** The words of a prompt: a message and its parameters. */
export type PromptText = Readonly<{ key: MessageKey; parameters: Readonly<Record<string, string>> }>;

/** The key of a use, which an answer names: the secret and the use. */
export function useKey(use: Pick<PromptedUse, "name" | "use">): string {
  return `${use.name.toUpperCase()}\n${use.use ?? ""}`;
}

/** The uses the prompt asks about, the most urgent first: those something waits for, the one that ends first first, then the oldest. */
export function promptedUses(pending: readonly PromptedUse[] | null | undefined): PromptedUse[] {
  return (pending ?? []).filter(use => use.prompt).sort((left, right) => {
    const awaited = Number(!!right.awaitedUntil) - Number(!!left.awaitedUntil);
    if (awaited !== 0) return awaited;
    if (left.awaitedUntil && right.awaitedUntil && left.awaitedUntil !== right.awaitedUntil) return left.awaitedUntil < right.awaitedUntil ? -1 : 1;
    return left.at < right.at ? -1 : left.at > right.at ? 1 : 0;
  });
}

/** Who asks for the secret, and what waits for the answer: the shell of a session by its title, else the use as the host names it. */
export function whoAsks(use: PromptedUse, sessionTitle: (id: string) => string | null): PromptText {
  if (use.sessionId) {
    const title = sessionTitle(use.sessionId)?.trim();
    return use.awaitedUntil
      ? { key: "The shell of the session “{title}” waits for your answer to start.", parameters: { title: title || use.sessionId } }
      : { key: "Asked by the shell of the session “{title}”.", parameters: { title: title || use.sessionId } };
  }
  if (use.use === "shell of a session") return { key: "Asked by the shell of a session.", parameters: {} };
  return use.use ? { key: "Asked for the {use}.", parameters: { use: use.use } } : { key: "Asked for a use nobody named.", parameters: {} };
}

/** What happens without an answer, for a use something waits for: it goes on without the secret at that time. */
export function withoutAnswer(use: PromptedUse, locale: string): PromptText | null {
  if (!use.awaitedUntil) return null;
  const time = new Date(use.awaitedUntil);
  if (Number.isNaN(time.getTime())) return null;
  return { key: "Without an answer by {time}, it starts without the secret.", parameters: { time: time.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" }) } };
}

/** What the prompt says after Confirm…, from the status of the confirmation; null when it was confirmed. */
export function confirmationNote(status: string): PromptText | null {
  if (status === "ok") return null;
  if (status === "canceled") return { key: "The verification was canceled: confirm again, or deny.", parameters: {} };
  if (status === "unavailable") return { key: "The system cannot verify you now.", parameters: {} };
  return { key: "The answer could not be sent.", parameters: {} };
}
