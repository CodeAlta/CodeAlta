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
 * What tells the user that a session is done or waits for them while CodeAlta is in the background, as the host names
 * it: a notification and the mark of the application, the mark alone, or nothing.
 */
export const turnAlertModes = ["all", "mark", "off"] as const;
export type TurnAlertMode = typeof turnAlertModes[number];

/** The mode the host names; a name this page does not know is the default, a notification and the mark. */
export const turnAlertMode = (value: string | null | undefined): TurnAlertMode => turnAlertModes.find(mode => mode === value) ?? "all";

/** The modes a platform offers: Linux has no mark of the application, only its notifications. */
export const turnAlertChoices = (platform: string): readonly TurnAlertMode[] => platform === "linux" ? ["all", "off"] : turnAlertModes;

/** The name of each mode where it is chosen, with the mark as each platform shows it. */
export function turnAlertLabel(mode: TurnAlertMode, platform: string): MessageKey {
  if (mode === "off") return "Off";
  if (platform === "macos") return mode === "all" ? "Notification and Dock badge" : "Dock badge only";
  if (platform === "windows") return mode === "all" ? "Notification and flashing taskbar button" : "Flashing taskbar button only";
  return mode === "all" ? "Notification" : "App icon mark only";
}

/** What a mode does, under its choice. */
export function turnAlertHelp(mode: TurnAlertMode, platform: string): MessageKey {
  if (mode === "off") return "CodeAlta does not tell you when a session is done or waits for you while it is in the background.";
  if (mode === "mark") return "While CodeAlta is in the background. The mark goes once you are back in CodeAlta.";
  return platform === "linux" ? "While CodeAlta is in the background. The notification opens the session."
    : "While CodeAlta is in the background. The notification opens the session; the mark goes once you are back in CodeAlta.";
}

/**
 * The button an arrow key moves to in a row of `count` buttons, from the one at `index`: the next or the
 * previous one, around the ends, as in a dialog of the system. -1 for any other key.
 */
export function nextChoice(index: number, key: string, count: number): number {
  const step = key === "ArrowRight" || key === "ArrowDown" ? 1 : key === "ArrowLeft" || key === "ArrowUp" ? -1 : 0;
  return step === 0 || index < 0 || index >= count ? -1 : (index + step + count) % count;
}

/**
 * What the question about the closed window says: where the application stays when it keeps running. On macOS
 * that is the Dock when the application has no icon in the menu bar (`trayIcon` is false).
 */
export function closeQuestion(platform: string, trayIcon = true): MessageKey {
  return platform === "macos" ? trayIcon ? "With its window closed, CodeAlta stays in the menu bar and sessions keep running."
      : "With its window closed, CodeAlta stays in the Dock and sessions keep running."
    : platform === "linux" ? "With its window closed, CodeAlta stays in the system tray and sessions keep running."
    : "With its window closed, CodeAlta stays in the notification area and sessions keep running.";
}

/** Where the application stays when its window is closed, as each platform calls it. */
export function keepRunningPlace(platform: string, trayIcon = true): MessageKey {
  return platform === "macos" ? trayIcon ? "CodeAlta stays in the menu bar. Sessions keep running." : "CodeAlta stays in the Dock. Sessions keep running."
    : platform === "linux" ? "CodeAlta stays in the system tray. Sessions keep running."
    : "CodeAlta stays in the notification area. Sessions keep running.";
}

/**
 * Whether the platform is told with a picture instead of a line: on macOS the application is a bundle in a
 * folder few people open, and it gets into the Dock by a gesture, which the Dock offers no other way for.
 */
export function entryAddedGuide(platform: string): boolean { return platform === "macos"; }

/** Where the application was just added, so it can be started like any other; macOS has its guide instead. */
export function entryAddedNotice(platform: string): MessageKey {
  return platform === "linux" ? "CodeAlta was added to the applications menu." : "CodeAlta was added to the Start Menu.";
}
