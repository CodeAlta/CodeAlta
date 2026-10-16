import type { MessageKey } from "./localization";

/** The name of the place a skill comes from, by the source the host reports, as Settings and the search show it. */
export const skillSourceLabels: Record<string, MessageKey> = { ProjectAlta: "Project", ProjectCommon: "Project", ProjectCopilot: "Project", UserAlta: "User", UserCommon: "User", UserCopilot: "User",
  Plugin: "Plugin", Builtin: "Built-in" };
