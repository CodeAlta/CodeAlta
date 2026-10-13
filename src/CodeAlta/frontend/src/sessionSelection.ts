import type { SessionChoicesResponse, SessionSelection } from "#neoastra";
import { offeredPermissionModes, providerPermissionMode } from "./permissionModes";

/**
 * A selection names a model its provider lists and one of the efforts of that model. There is no "default" one:
 * only a provider that lists no model leaves the model open, and only a model without efforts leaves the effort open.
 * A model that lists no effort takes the one the session holds, or none: its provider did not say which efforts it
 * has, the session was given one elsewhere (the alta tool, an automation, a work item), and the host passes it on.
 * The composer shows that effort and offers no other.
 */
export function validSelection(choices: SessionChoicesResponse, value: SessionSelection): boolean {
  if (value.providerKey !== choices.current?.providerKey || !choices.prompts.some(p => p.id === value.agentPromptId)) return false;
  if (!validPermissionMode(choices, value.permissionMode)) return false;
  if (value.modelId === null) return value.reasoningEffort === null && choices.models.length === 0;
  const model = choices.models.find(m => m.id === value.modelId);
  return !!model && (model.efforts.length === 0 || value.reasoningEffort !== null && model.efforts.includes(value.reasoningEffort));
}

/**
 * A selection kept without a model or without an effort takes what the session runs with: its own model and
 * effort, or the effort its model starts with.
 */
export function completeSelection(choices: SessionChoicesResponse, value: SessionSelection): SessionSelection {
  const current = choices.current;
  if (!current || value.providerKey !== current.providerKey) return value;
  // A mode the provider no longer offers is forgotten, not the rest of the selection: the session keeps its own.
  if (!validPermissionMode(choices, value.permissionMode)) value = { ...value, permissionMode: null };
  if (value.modelId === null)
    return current.modelId === null ? value : { ...value, modelId: current.modelId, reasoningEffort: current.reasoningEffort };
  const start = value.reasoningEffort === null ? choices.models.find(model => model.id === value.modelId)?.startEffort : null;
  return start ? { ...value, reasoningEffort: start } : value;
}

export function restoreSelection(load: () => string | null, choices: SessionChoicesResponse): SessionSelection | null {
  try {
    const stored = JSON.parse(load() ?? "null") as SessionSelection | null;
    const value = stored && completeSelection(choices, stored);
    return value && validSelection(choices, value) ? value : null;
  } catch { return null; }
}

export function changeSelection(choices: SessionChoicesResponse, current: SessionSelection,
  field: "agentPromptId" | "modelId" | "reasoningEffort" | "permissionMode", value: string): SessionSelection | null {
  const next = { ...current, [field]: value || null };
  // Another model starts with its own effort. It keeps the permission mode: the mode belongs to the session.
  if (field === "modelId") next.reasoningEffort = choices.models.find(model => model.id === next.modelId)?.startEffort ?? null;
  // A saved mode the provider no longer offers does not hold back another change: the session keeps it.
  if (field !== "permissionMode" && !validPermissionMode(choices, next.permissionMode)) next.permissionMode = null;
  // The mode the session already has is no change; going back to the provider's from another one is named. The mode
  // is checked alone: it is the session's and goes to the host by itself, so a model or an effort the provider no
  // longer lists does not hold it back. The rest of the selection stays as it was, and is checked when it is sent.
  if (field === "permissionMode") {
    next.permissionMode = (value || null) === (choices.current?.permissionMode ?? null) ? null : value || providerPermissionMode;
    return validPermissionMode(choices, next.permissionMode) ? next : null;
  }
  return validSelection(choices, next) ? next : null;
}

// No mode keeps the session's. A mode is one the provider offers; the provider's own is only chosen when it offers some.
function validPermissionMode(choices: SessionChoicesResponse, mode: string | null | undefined): boolean {
  if (mode == null) return true;
  const offered = offeredPermissionModes(choices);
  return offered.length > 0 && (mode === providerPermissionMode || offered.some(choice => choice.id === mode));
}
