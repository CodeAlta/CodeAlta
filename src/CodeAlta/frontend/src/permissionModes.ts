import type { SessionChoicesResponse, SessionPermissionModeChoice, SessionSelection } from "#neoastra";
import type { MessageKey } from "./localization";

/**
 * The permission mode of a selection that takes the session back to the mode its provider is configured with. It is
 * never the id of a mode (OwnedSessionSelection.ProviderPermissionMode on the host).
 */
export const providerPermissionMode = "provider";

// The modes by their id: those of the Claude Code CLI, three of which every provider has because CodeAlta answers
// the requests of its sessions (default, acceptEdits, bypassPermissions). Their names and descriptions are the keys
// of the localization.
const modes: Readonly<Record<string, Readonly<{ name: MessageKey; description: MessageKey }>>> = Object.freeze({
  default: { name: "Ask first", description: "Always ask before making changes." },
  acceptEdits: { name: "Accept edits", description: "Accept file edits; ask before commands." },
  plan: { name: "Plan", description: "Claude Code plans and changes nothing." },
  auto: { name: "Auto", description: "Claude Code handles permission decisions." },
  dontAsk: { name: "Don't ask", description: "Never ask; refuse what is not already allowed." },
  bypassPermissions: { name: "Bypass permissions", description: "Accept all permissions." },
});

/** The mode in which commands and file changes wait for the user, and the one in which nothing does. */
export const askPermissionMode = "default";
export const bypassPermissionMode = "bypassPermissions";

/** The localization key of the name of a mode, or null for a mode CodeAlta does not know: its id names it. */
export function permissionModeName(id: string): MessageKey | null {
  return Object.hasOwn(modes, id) ? modes[id].name : null;
}

/** The localization key of the one-line description of a mode, or null for a mode CodeAlta does not know. */
export function permissionModeDescription(id: string): MessageKey | null {
  return Object.hasOwn(modes, id) ? modes[id].description : null;
}

/** The modes a session can be given; none where the host offers no choice, whose composer hides it. */
export function offeredPermissionModes(choices: SessionChoicesResponse | undefined): readonly SessionPermissionModeChoice[] {
  return choices?.permissionModes ?? [];
}

/**
 * The mode the next Send runs the session in, chosen for it: an id, or null for the default one (the mode of its
 * provider, else the one of the application, which the host reports as `defaultPermissionMode`).
 */
export function chosenPermissionMode(choices: SessionChoicesResponse, value: SessionSelection): string | null {
  const mode = value.permissionMode ?? choices.current?.permissionMode ?? null;
  return mode === providerPermissionMode ? null : mode;
}
