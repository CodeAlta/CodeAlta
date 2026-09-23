import type { SessionChoicesResponse, SessionSelection } from "#neoastra";

export function validSelection(choices: SessionChoicesResponse, value: SessionSelection): boolean {
  if (value.providerKey !== choices.current?.providerKey || !choices.prompts.some(p => p.id === value.agentPromptId)) return false;
  if (value.modelId === null) return value.reasoningEffort === null;
  const model = choices.models.find(m => m.id === value.modelId);
  return !!model && (value.reasoningEffort === null || model.efforts.includes(value.reasoningEffort));
}

export function restoreSelection(load: () => string | null, choices: SessionChoicesResponse): SessionSelection | null {
  try {
    const value = JSON.parse(load() ?? "null") as SessionSelection | null;
    return value && validSelection(choices, value) ? value : null;
  } catch { return null; }
}

export function changeSelection(choices: SessionChoicesResponse, current: SessionSelection,
  field: "agentPromptId" | "modelId" | "reasoningEffort", value: string): SessionSelection | null {
  const next = { ...current, [field]: value || null };
  if (field === "modelId") next.reasoningEffort = null;
  return validSelection(choices, next) ? next : null;
}
