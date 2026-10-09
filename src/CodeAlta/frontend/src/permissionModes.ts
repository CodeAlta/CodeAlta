import type { SessionChoicesResponse, SessionPermissionModeChoice, SessionSelection } from "#neoastra";
import type { MessageKey } from "./localization";

/**
 * The permission mode of a selection that takes the session back to the mode its provider is configured with. It is
 * never the id of a mode (OwnedSessionSelection.ProviderPermissionMode on the host).
 */
export const providerPermissionMode = "provider";

// The modes of the Claude Code CLI, by their id. Their names and descriptions are the keys of the localization.
const modes: Readonly<Record<string, Readonly<{ name: MessageKey; description: MessageKey }>>> = Object.freeze({
  default: { name: "Default", description: "Claude Code asks before it runs a command or changes a file; CodeAlta reviews each request." },
  acceptEdits: { name: "Accept edits", description: "Claude Code changes files without CodeAlta's review; it still asks before commands." },
  plan: { name: "Plan", description: "Claude Code plans and changes nothing." },
  auto: { name: "Auto", description: "A classifier of Claude Code allows or refuses each request, without CodeAlta's review." },
  dontAsk: { name: "Don't ask", description: "Claude Code never asks: it runs what its own settings allow and refuses the rest, without CodeAlta's review." },
  bypassPermissions: { name: "Bypass permissions", description: "Claude Code runs everything without asking, and without CodeAlta's review." },
});

/** The localization key of the name of a mode, or null for a mode CodeAlta does not know: its id names it. */
export function permissionModeName(id: string): MessageKey | null {
  return Object.hasOwn(modes, id) ? modes[id].name : null;
}

/** The localization key of the one-line description of a mode, or null for a mode CodeAlta does not know. */
export function permissionModeDescription(id: string): MessageKey | null {
  return Object.hasOwn(modes, id) ? modes[id].description : null;
}

/** The modes a session of the provider can be given; none for a provider without modes, whose composer hides the choice. */
export function offeredPermissionModes(choices: SessionChoicesResponse | undefined): readonly SessionPermissionModeChoice[] {
  return choices?.permissionModes ?? [];
}

/** The mode the next Send runs the session in, chosen for it: an id, or null for the mode of its provider. */
export function chosenPermissionMode(choices: SessionChoicesResponse, value: SessionSelection): string | null {
  const mode = value.permissionMode ?? choices.current?.permissionMode ?? null;
  return mode === providerPermissionMode ? null : mode;
}

/**
 * What the session runs in with this selection: the chosen mode, else the one the provider is configured with (null
 * when it leaves it to the CLI), and whether Claude Code then runs requests without CodeAlta's review.
 */
export function effectivePermissionMode(choices: SessionChoicesResponse, value: SessionSelection): Readonly<{ id: string | null; chosen: boolean; skipsReview: boolean }> {
  const chosen = chosenPermissionMode(choices, value);
  const id = chosen ?? choices.defaultPermissionMode ?? null;
  // The host says which modes skip the review; the provider's own mode is listed unless a session cannot be given it (plan).
  return { id, chosen: chosen !== null, skipsReview: id !== null && offeredPermissionModes(choices).some(mode => mode.id === id && mode.skipsReview) };
}
