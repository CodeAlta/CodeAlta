// The folders of skills the user adds on the Skills page: what their scope, their state and the refusals of the host say.
import type { SkillsFolder } from "#neoastra";
import type { MessageKey } from "./localization";
import type { SettingsNotice } from "./settingsEditing";

/** Where a folder of skills is read: for every space, for the space the window shows, or for the selected project. */
export type SkillFolderScope = "Global" | "Space" | "Project";

/** The scopes a folder can be added to: the space when the window has more than one, the project when one is selected. */
export function skillFolderScopes(hasSpaces: boolean, hasProject: boolean): readonly SkillFolderScope[] {
  return ["Global", ...(hasSpaces ? ["Space" as const] : []), ...(hasProject ? ["Project" as const] : [])];
}

/** What the tag of a folder says when no skill is read from it; null for a folder that is read. */
export function skillFolderStateLabel(state: string): { label: MessageKey; title: MessageKey } | null {
  switch (state) {
    case "ready": return null;
    case "missing": return { label: "Not found", title: "Nothing is at this path any more: no skill is read from it." };
    case "unreadable": return { label: "Cannot be read", title: "The system refuses to list this folder: no skill is read from it." };
    default: return { label: "Not read", title: "This path cannot be a folder of skills: no skill is read from it." };
  }
}

/** Why the host refused to add a folder (`folder_refused` names the problem in its message). */
const refusals: Readonly<Record<string, MessageKey>> = Object.freeze({
  Disk: "A whole disk cannot be a folder of skills.",
  Home: "The home folder cannot be a folder of skills: choose the folder that holds the skills.",
  CodeAlta: "The folder of CodeAlta cannot be a folder of skills: its skills are already read.",
});

/**
 * What the page says of a status of `addFolder` or `removeFolder` of its own; undefined for the statuses every page
 * shares, which `settingsFailure` says.
 */
export function skillFolderNotice(status: string, message?: string | null): SettingsNotice | undefined {
  switch (status) {
    case "folder_missing": return { key: "The folder does not exist.", intent: "warning" };
    case "folder_refused": return { key: refusals[message ?? ""] ?? "Give the full path of the folder.", intent: "warning" };
    case "folder_listed": return { key: "This folder is already in the list.", intent: "warning" };
    case "folder_limit": return { key: "A list holds at most 32 folders of skills.", intent: "warning" };
    default: return undefined;
  }
}

/** The folders of a scope, in the order they are read. */
export const foldersOf = (folders: readonly SkillsFolder[], scope: SkillFolderScope) => folders.filter(folder => folder.scope === scope);
