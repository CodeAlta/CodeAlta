import type { MessageKey } from "./localization";

type Message = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string | number>> }>;

/** The question before an exit that stops running sessions, or ends what terminals run: a sentence for each. */
export function exitQuestion(runningSessions: number, busyTerminals = 0): Message[] {
  const questions: Message[] = [];
  if (runningSessions === 1) questions.push({ key: "A session is running. Exiting CodeAlta stops it." });
  else if (runningSessions > 1 || busyTerminals <= 0) questions.push({ key: "{count} sessions are running. Exiting CodeAlta stops them.", parameters: { count: runningSessions } });
  if (busyTerminals === 1) questions.push({ key: "A terminal is running a command. Exiting CodeAlta ends it." });
  else if (busyTerminals > 1) questions.push({ key: "{count} terminals are running a command. Exiting CodeAlta ends them.", parameters: { count: busyTerminals } });
  return questions;
}

/** What closing the window does, as the host names it: the page asks, the application keeps running, or it exits. */
export const closeBehaviors = ["ask", "keep", "exit"] as const;
export type CloseBehavior = typeof closeBehaviors[number];

/** The behavior the host names; a name this page does not know asks. */
export const closeBehavior = (value: string): CloseBehavior => closeBehaviors.find(behavior => behavior === value) ?? "ask";

/** The name of each behavior where it is chosen. */
export function closeBehaviorLabel(behavior: CloseBehavior): MessageKey {
  return behavior === "keep" ? "Keep running" : behavior === "exit" ? "Exit CodeAlta" : "Ask each time";
}

/**
 * The button an arrow key moves to in a row of `count` buttons, from the one at `index`: the next or the
 * previous one, around the ends, as in a dialog of the system. -1 for any other key.
 */
export function nextChoice(index: number, key: string, count: number): number {
  const step = key === "ArrowRight" || key === "ArrowDown" ? 1 : key === "ArrowLeft" || key === "ArrowUp" ? -1 : 0;
  return step === 0 || index < 0 || index >= count ? -1 : (index + step + count) % count;
}

/** What the question about the closed window says: where the application stays when it keeps running. */
export function closeQuestion(platform: string): MessageKey {
  return platform === "macos" ? "With its window closed, CodeAlta stays in the menu bar and sessions keep running."
    : platform === "linux" ? "With its window closed, CodeAlta stays in the system tray and sessions keep running."
    : "With its window closed, CodeAlta stays in the notification area and sessions keep running.";
}

/** Where the application stays when its window is closed, as each platform calls it. */
export function keepRunningPlace(platform: string): MessageKey {
  return platform === "macos" ? "CodeAlta stays in the menu bar. Sessions keep running."
    : platform === "linux" ? "CodeAlta stays in the system tray. Sessions keep running."
    : "CodeAlta stays in the notification area. Sessions keep running.";
}

/** Where the application was just added, so it can be started like any other. */
export function entryAddedNotice(platform: string): MessageKey {
  return platform === "macos" ? "CodeAlta was added to your Applications folder: open it from the Finder, Spotlight or Launchpad."
    : platform === "linux" ? "CodeAlta was added to the applications menu."
    : "CodeAlta was added to the Start Menu.";
}
