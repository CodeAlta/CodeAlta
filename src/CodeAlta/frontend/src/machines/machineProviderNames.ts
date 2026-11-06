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
 * The catalogs of the Agent chip of an automation placed on a machine: the models are those the machine lists for its
 * provider; the agent prompts, permission modes and sandbox are read as this computer has them.
 */
export function onMachine(api: SessionSettingsApi, machineId: string, choices: Pick<MachineNewSessionChoices, "providers">): SessionSettingsApi {
  return {
    ...api,
    key: JSON.stringify([api.key, machineId]),
    // The machine says nothing of the order of its models: they are listed as it gives them.
    models: async providerId => ({ models: choices.providers.find(provider => provider.key === providerId)?.models
      .map(model => ({ id: model.id, name: model.name, efforts: [], defaultEffort: null })) ?? [], order: null }),
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
