import type { ChoiceModel, PermissionModeOption, PromptSelection, Session, SessionChoices } from "./contract";

// The selection of a session's composer, without the DOM: what the session runs on (its choices, else what the list says), what
// was chosen on the phone for the next send or queue, and what that send carries.

/**
 * What was chosen on the phone and not sent yet. A permission mode of `providerPermissionMode` takes the session back to the
 * mode of its provider.
 */
export type Picked = Readonly<{ model?: string; reasoning?: string; agent?: string; permissionMode?: string }>;

/** The permission mode that takes a session back to its provider's (never the id of a mode). */
export const providerPermissionMode = "provider";

/** What the composer shows: the choices of the session with what was picked over them. */
export type Shown = Readonly<{
  providerName: string | null; model: string | null; modelName: string | null; reasoning: string | null;
  agent: string | null; agentName: string | null; permissionMode: string | null;
}>;

/** What the composer shows of a session: what was picked, else its choices, else what the list says of it. */
export function shownSelection(session: Pick<Session, "model" | "agent"> | null | undefined, choices: SessionChoices | null | undefined, picked: Picked = {}): Shown {
  const current = choices?.current;
  const model = picked.model ?? current?.model ?? session?.model?.id ?? null;
  const agent = picked.agent ?? current?.agent ?? session?.agent?.id ?? null;
  const mode = picked.permissionMode ?? current?.permissionMode ?? null;
  return {
    providerName: current?.providerName ?? session?.model?.providerName ?? current?.provider ?? session?.model?.provider ?? null,
    model,
    modelName: choices?.models.find(m => m.id === model)?.name ?? (model === current?.model ? current?.modelName : null) ?? (model === session?.model?.id ? session?.model?.name : null) ?? model,
    reasoning: picked.reasoning ?? (picked.model ? null : current?.reasoning ?? session?.model?.reasoning ?? null),
    agent,
    agentName: choices?.prompts.find(p => p.id === agent)?.name ?? (agent === session?.agent?.id ? session?.agent?.name : null) ?? agent,
    permissionMode: mode === providerPermissionMode ? null : mode,
  };
}

/** The words of the chip: the model and its effort, and the agent prompt when it is not the default one. */
export function chipText(shown: Shown): Readonly<{ model: string; agent: string | null }> {
  const model = [shown.modelName, shown.reasoning].filter(Boolean).join(" · ") || "Model";
  const agent = shown.agent && shown.agent.toLowerCase() !== "default" ? shown.agentName || shown.agent : null;
  return { model, agent };
}

/** What a pick changes of the session: each value that differs from what it runs on, nothing for the rest. */
export function pickedChanges(choices: SessionChoices | null | undefined, picked: Picked): PromptSelection {
  const current = choices?.current;
  const changes: Record<string, string> = {};
  if (picked.model && picked.model !== current?.model) {
    changes.model = picked.model;
    // A new model goes with an effort it has, the one shown.
    if (picked.reasoning) changes.reasoning = picked.reasoning;
  } else if (picked.reasoning && picked.reasoning !== current?.reasoning) changes.reasoning = picked.reasoning;
  if (picked.agent && picked.agent !== current?.agent) changes.agent = picked.agent;
  if (picked.permissionMode && (picked.permissionMode === providerPermissionMode ? current?.permissionMode != null : picked.permissionMode !== current?.permissionMode))
    changes.permissionMode = picked.permissionMode;
  return changes;
}

/** What the session runs on once a send carried a selection, until its choices are read again. */
export function appliedSelection(choices: SessionChoices, sent: PromptSelection): SessionChoices["current"] {
  const current = choices.current;
  return {
    ...current,
    ...(sent.model ? { model: sent.model, modelName: choices.models.find(m => m.id === sent.model)?.name ?? sent.model, reasoning: sent.reasoning ?? null } : {}),
    ...(!sent.model && sent.reasoning ? { reasoning: sent.reasoning } : {}),
    ...(sent.agent ? { agent: sent.agent, agentName: choices.prompts.find(p => p.id === sent.agent)?.name ?? sent.agent } : {}),
    ...(sent.permissionMode ? { permissionMode: sent.permissionMode === providerPermissionMode ? null : sent.permissionMode } : {}),
  };
}

/** Whether something picked waits for the next send. */
export const isPending = (choices: SessionChoices | null | undefined, picked: Picked): boolean => Object.keys(pickedChanges(choices, picked)).length > 0;

/** The effort a model is given: the one shown when the model has it, else its start effort, else its first; none when it has none. */
export function effortFor(model: ChoiceModel | undefined, wanted: string | null | undefined): string | undefined {
  const efforts = model?.efforts ?? [];
  return [wanted, model?.startEffort].find((effort): effort is string => !!effort && efforts.includes(effort)) ?? efforts[0];
}

/** The pick of a model: its effort follows (kept when it has it). */
export function pickSessionModel(choices: SessionChoices, picked: Picked, shown: Shown, modelId: string): Picked {
  const model = choices.models.find(m => m.id === modelId);
  return { ...picked, model: modelId, reasoning: effortFor(model, shown.reasoning) };
}

// The modes by their id, named as the desktop names them (permissionModes.ts).
const modeNames: Readonly<Record<string, Readonly<{ name: string; description: string }>>> = Object.freeze({
  default: { name: "Ask first", description: "Always ask before making changes." },
  acceptEdits: { name: "Accept edits", description: "Accept file edits; ask before commands." },
  plan: { name: "Plan", description: "Claude Code plans and changes nothing." },
  auto: { name: "Auto", description: "Claude Code handles permission decisions." },
  dontAsk: { name: "Don't ask", description: "Never ask; refuse what is not already allowed." },
  bypassPermissions: { name: "Bypass permissions", description: "Accept all permissions." },
});

/** The name of a mode: the one alta gives, else the desktop's, else its id; null is the provider's own. */
export function modeName(mode: string | null, offered: readonly PermissionModeOption[] = []): string {
  if (!mode) return "The provider's setting";
  return offered.find(m => m.id === mode)?.name || (Object.hasOwn(modeNames, mode) ? modeNames[mode].name : mode);
}

/** The one-line description of a mode, when known. */
export function modeDescription(mode: string | null, offered: readonly PermissionModeOption[] = []): string {
  if (!mode) return "As the provider is configured.";
  return offered.find(m => m.id === mode)?.description || (Object.hasOwn(modeNames, mode) ? modeNames[mode].description : "");
}

/** The key of the phone's choice to queue a prompt rather than steer it while a turn runs. */
export const queueFirstKey = "alta.queueFirst";
