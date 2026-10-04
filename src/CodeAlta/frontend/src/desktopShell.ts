import type { MessageKey } from "./localization";

type Message = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string | number>> }>;

/** The question before an exit that stops running sessions. */
export function exitQuestion(runningSessions: number): Message {
  return runningSessions === 1 ? { key: "A session is running. Exiting CodeAlta stops it." }
    : { key: "{count} sessions are running. Exiting CodeAlta stops them.", parameters: { count: runningSessions } };
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
