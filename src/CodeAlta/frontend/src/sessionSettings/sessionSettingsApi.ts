// What the form of the Agent chip of a session started by an automation, a task or an issue reads of the host.
import { globalConfig, modelCatalog, sandboxSettings, workspace, type SandboxSessionResponse, type SessionPermissionModeChoice, type SessionPromptChoice } from "#neoastra";
import type { RunModel } from "../workItems/runsWith";

/** A model of a provider, as the form lists it. */
export type SessionSettingsModel = RunModel & Readonly<{ name?: string; effortsComplete?: boolean | null }>;

/** The catalogs of the form; each answers null (or an empty list) when it cannot be read, and the form then leaves its row out. */
export type SessionSettingsApi = Readonly<{
  /** Names the host, the project and the space the catalogs are of: catalogs of another key are read again. */
  key: string;
  models: (providerId: string, signal: AbortSignal) => Promise<readonly SessionSettingsModel[]>;
  prompts: (signal: AbortSignal) => Promise<Readonly<{ prompts: readonly SessionPromptChoice[]; defaultPromptId: string | null }> | null>;
  permissionModes: (providerId: string, signal: AbortSignal) => Promise<Readonly<{ modes: readonly SessionPermissionModeChoice[]; defaultMode: string | null }> | null>;
  reviewSetting: (signal: AbortSignal) => Promise<boolean | null>;
  /** The sandbox a session of that provider and model gets from the settings, with the profiles it can choose. */
  sandbox: (providerId: string | null, modelId: string | null, signal: AbortSignal) => Promise<SandboxSessionResponse | null>;
}>;

/**
 * The catalogs of the host for a session of a project (null for a chat) in a space (null for the one its project is
 * in, else the default one).
 */
export function sessionSettingsApi(epoch: string, projectId: string | null, projectPath: string | null, spaceId: string | null): SessionSettingsApi {
  const options = (signal: AbortSignal) => ({ signal, timeoutMilliseconds: 15000 });
  return {
    key: JSON.stringify([epoch, projectId, projectPath, spaceId]),
    models: (providerId, signal) => modelCatalog.models({ expectedEpoch: epoch, providerId }, options(signal))
      .then(reply => reply.status === "ok" ? reply.models : []),
    prompts: signal => workspace.draftPrompts({ expectedHostEpoch: epoch, projectId, projectPath, spaceId }, options(signal))
      .then(reply => reply.status === "ok" ? { prompts: reply.prompts, defaultPromptId: reply.defaultPromptId } : null),
    permissionModes: (providerId, signal) => workspace.draftPermissionModes({ expectedHostEpoch: epoch, providerId }, options(signal))
      .then(reply => reply.status === "ok" ? { modes: reply.permissionModes, defaultMode: reply.defaultPermissionMode } : null),
    reviewSetting: signal => globalConfig.reviewSettings({ expectedEpoch: epoch }, options(signal))
      .then(reply => reply.status === "ok" ? reply.beforeCommit : null),
    sandbox: (providerId, modelId, signal) => sandboxSettings.draft({ expectedEpoch: epoch, providerKey: providerId, modelId, projectId, spaceId, own: null }, options(signal))
      .then(reply => reply.status === "ok" ? reply : null),
  };
}
