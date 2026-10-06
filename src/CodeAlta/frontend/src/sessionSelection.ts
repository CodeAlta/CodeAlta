import type { SessionChoicesResponse, SessionSelection } from "#neoastra";

/**
 * A selection names a model its provider lists and one of the efforts of that model. There is no "default" one:
 * only a provider that lists no model leaves the model open, and only a model without efforts leaves the effort open.
 */
export function validSelection(choices: SessionChoicesResponse, value: SessionSelection): boolean {
  if (value.providerKey !== choices.current?.providerKey || !choices.prompts.some(p => p.id === value.agentPromptId)) return false;
  if (value.modelId === null) return value.reasoningEffort === null && choices.models.length === 0;
  const model = choices.models.find(m => m.id === value.modelId);
  return !!model && (value.reasoningEffort === null ? model.efforts.length === 0 : model.efforts.includes(value.reasoningEffort));
}

/**
 * A selection kept without a model or without an effort takes what the session runs with: its own model and
 * effort, or the effort its model starts with.
 */
export function completeSelection(choices: SessionChoicesResponse, value: SessionSelection): SessionSelection {
  const current = choices.current;
  if (!current || value.providerKey !== current.providerKey) return value;
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
  field: "agentPromptId" | "modelId" | "reasoningEffort", value: string): SessionSelection | null {
  const next = { ...current, [field]: value || null };
  // Another model starts with its own effort.
  if (field === "modelId") next.reasoningEffort = choices.models.find(model => model.id === next.modelId)?.startEffort ?? null;
  return validSelection(choices, next) ? next : null;
}
