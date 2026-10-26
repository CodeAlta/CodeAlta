import { useEffect, useState, useSyncExternalStore } from "react";
import { Card, HTMLSelect } from "@blueprintjs/core";
import { agentPrompts, sandboxSettings, type AgentPromptEntry } from "#neoastra";
import { SandboxScopeSelect, useSandboxScopes, type SandboxScopeApi } from "../sandbox/SandboxScopeControls";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { SpaceModelTiers } from "./SpaceModelTiers";
import type { SpaceSectionProps } from "./spaceSection";
import { inSpace, resourceSpaces } from "./spaces";

/** The agent prompts a new session can start with: those of the user, one per id. */
export async function readAgentPrompts(epoch: string): Promise<readonly AgentPromptEntry[] | null> {
  try {
    const reply = await agentPrompts.list({ expectedEpoch: epoch, projectId: null }, { timeoutMilliseconds: 15_000 });
    return reply.status === "ok" ? reply.prompts.filter(prompt => prompt.kind === "Agent" && !prompt.shadowed) : null;
  } catch { return null; }
}

/**
 * The section "New sessions" of the page of a space: the agent prompt its new sessions start with when none is
 * chosen, among the agent prompts of the space, the sandbox they run in, and the model tiers they use. The choice of the user in the window,
 * and the prompt an agent or an automation names, still win.
 */
export function SpaceNewSessions({ read = readAgentPrompts, sandbox = sandboxSettings, ...section }: SpaceSectionProps & Readonly<{
  /** Reads the agent prompts; the page of Settings by default. */
  read?: (epoch: string) => Promise<readonly AgentPromptEntry[] | null>;
  /** The sandbox of the sessions of the space; the host by default. */
  sandbox?: SandboxScopeApi;
}>) {
  const { space, hub, canEdit, busy, run } = section;
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const epoch = hub.hostEpoch();
  const sandboxes = useSandboxScopes(epoch, sandbox);
  const [prompts, setPrompts] = useState<readonly AgentPromptEntry[] | null>(null);
  useEffect(() => {
    if (!epoch) return;
    let alive = true;
    void read(epoch).then(value => { if (alive) setPrompts(value); });
    return () => { alive = false; };
  }, [epoch, read]);
  if (!state.available) return null;
  const chosen = space.agentPrompt ?? "default";
  const offered = (prompts ?? []).filter(prompt => inSpace(resourceSpaces(state.assignments, "prompt", prompt.id), space.id));
  // What the space names stays shown, also when the space no longer has it: the user sees it and chooses another.
  const listed = offered.some(prompt => prompt.id === chosen) ? offered : [...offered, { id: chosen, name: chosen } as AgentPromptEntry];
  const id = `space-${space.id}`;
  return <Card className="appearance-settings space-section" aria-labelledby={`${id}-new-sessions`}>
    <h2 id={`${id}-new-sessions`}>{t("New sessions")}</h2>
    <Field label={t("Agent prompt")} htmlFor={`${id}-agent-prompt`}
      notice={<p className="settings-field-help">{t("A new session of this space starts with it when none is chosen.")}</p>}>
      <HTMLSelect id={`${id}-agent-prompt`} className="space-agent-prompt" value={chosen} disabled={!canEdit || busy || prompts === null}
        onChange={event => { const next = event.currentTarget.value; void run(() => hub.setAgentPrompt(space.id, next === "default" ? null : next), true); }}>
        {listed.map(prompt => <option key={prompt.id} value={prompt.id}>{prompt.name || prompt.id}</option>)}
      </HTMLSelect>
    </Field>
    {sandboxes.listing && <Field label={t("Sandbox")} htmlFor={`${id}-sandbox`}
      notice={<p className="settings-field-help">{t("The sessions of this space run in it, unless their model, their provider or their project chooses another.")}</p>}>
      <SandboxScopeSelect id={`${id}-sandbox`} value={sandboxes.value("space", space.id)} profiles={sandboxes.profiles} disabled={!canEdit || busy || sandboxes.busy}
        label={t("Sandbox of {name}", { name: space.name })} onChange={value => void sandboxes.choose("space", space.id, null, value)} />
    </Field>}
    {/* Each part is a component of its own, so that each can change without the others. */}
    <SpaceModelTiers {...section} />
  </Card>;
}
