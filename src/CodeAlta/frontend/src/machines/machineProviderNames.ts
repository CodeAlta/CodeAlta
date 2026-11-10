// The providers of another machine where the window shows what a session or an automation runs with: named as that
// machine names them, with its models. A provider of the same key on this computer may be another account, with another
// name and other models.
import type { MachineNewSessionChoices, WorkspaceProject } from "#neoastra";
import type { SessionSettingsApi } from "../sessionSettings/sessionSettingsApi";
import type { RunProvider } from "../workItems/runsWith";

/** The ready providers of a machine as the Agent chip lists them; null while they are not read, or the machine cannot say. */
export function machineRunProviders(choices: Pick<MachineNewSessionChoices, "status" | "providers"> | null): readonly RunProvider[] | null {
  if (!choices || choices.status !== "ok") return null;
  return choices.providers.map(provider => ({ id: provider.key, name: provider.name, isDefault: provider.isDefault, defaultModel: provider.defaultModelId,
    defaultReasoning: null }));
}

/**
 * The catalogs of the Agent chip of a session that runs on a machine, as that machine says them: the models of its
 * providers with their efforts, its agent prompts, the permission modes it lets a session started from here have, and
 * its sandbox profiles. What an older machine does not say is read from `base` (this computer, for an automation),
 * else left out of the form (null). The review of the changes is the setting of `base`; none without it.
 */
export function onMachine(base: SessionSettingsApi | null, machineId: string,
  choices: Pick<MachineNewSessionChoices, "providers"> & Partial<Pick<MachineNewSessionChoices, "prompts" | "defaultPromptId" | "sandboxProfiles">>): SessionSettingsApi {
  const provider = (providerId: string | null) => choices.providers.find(candidate => candidate.key === providerId);
  return {
    key: JSON.stringify([base?.key ?? null, machineId, choices]),
    // The machine says nothing of the order of its models: they are listed as it gives them.
    models: async providerId => ({ models: provider(providerId)?.models
      .map(model => ({ id: model.id, name: model.name, efforts: model.efforts ?? [], defaultEffort: model.defaultEffort ?? null, effortsComplete: model.efforts ? true : null })) ?? [],
      order: null }),
    prompts: async signal => choices.prompts
      ? { prompts: choices.prompts.map(prompt => ({ id: prompt.id, name: prompt.name })), defaultPromptId: choices.defaultPromptId ?? null }
      : base ? base.prompts(signal) : null,
    permissionModes: async (providerId, signal) => {
      const named = provider(providerId);
      if (named?.permissionModes) return { modes: named.permissionModes.map(id => ({ id })), defaultMode: named.defaultPermissionMode ?? null };
      return base ? base.permissionModes(providerId, signal) : null;
    },
    reviewSetting: async signal => base ? base.reviewSetting(signal) : null,
    sandbox: async (providerId, modelId, signal) => {
      // A machine that cannot start a sandbox offers none: the row is left out.
      if (choices.sandboxProfiles) return choices.sandboxProfiles.length === 0 ? null : { status: "ok", own: null, problem: null,
        profile: provider(providerId)?.sandbox ?? null, source: provider(providerId)?.sandbox ? "machine" : "none", choices: ["off", ...choices.sandboxProfiles] };
      return base ? base.sandbox(providerId, modelId, signal) : null;
    },
  };
}

/** The projects an automation can run in: those of this computer's catalog, never the row that stands for a machine. */
export function automationProjects<T extends Pick<WorkspaceProject, "archived"> & { machineId?: string | null }>(projects: readonly T[]): T[] {
  return projects.filter(project => !project.archived && !project.machineId);
}

/**
 * The name the row of a session in the Explorer gives its provider: for a session of another machine, the name that
 * machine gives it, never the name of a provider of this computer with the same key; else the name of the configuration
 * of this computer, then its key.
 */
export function sessionRowProviderName(session: Readonly<{ providerKey?: string | null; machineId?: string | null; machineProviderName?: string | null }>,
  configured: string | null | undefined): string | null {
  return session.machineId ? session.machineProviderName ?? session.providerKey ?? null : configured ?? session.providerKey ?? null;
}

/**
 * The name the composer gives the provider of a session: for a session of another machine, the name that machine gives
 * it; else the name of the configuration of this computer, then the one its host gives, then its key. Null without one.
 */
export function sessionProviderName(providerKey: string | null | undefined,
  choices: Readonly<{ current?: Readonly<{ providerKey: string }> | null; providerName?: string | null; machineProvider?: boolean | null }> | undefined,
  configured: string | null | undefined): string | null {
  const given = providerKey && providerKey === choices?.current?.providerKey ? choices?.providerName ?? null : null;
  return choices?.machineProvider ? given ?? providerKey ?? null : configured ?? given ?? providerKey ?? null;
}
