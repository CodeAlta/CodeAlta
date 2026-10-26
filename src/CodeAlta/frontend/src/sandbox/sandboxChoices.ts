// What the window says of the sandbox of a session and of the places that choose one, from the answers of the
// `sandboxSettings` service of the host.
import type { sandboxSettings, SandboxProfileItem, SandboxScopeItem, SandboxSessionResponse } from "#neoastra";
import type { MessageKey } from "../localization";

/** The service of the host the sandbox controls use; the tests give their own. */
export type SandboxApi = Pick<typeof sandboxSettings, "read" | "saveAllSessions" | "saveProfile" | "deleteProfile" | "setScope" | "check">;
/** What the sandbox of one session, or of the one about to be created, is read and changed with. */
export type SandboxSessionApi = Pick<typeof sandboxSettings, "session" | "setSession" | "draft">;
/** Where a sandbox is chosen, besides a session. */
export type SandboxScopeKind = "provider" | "model" | "space" | "project";

/** The value of a session that follows its scopes, as `setSession` takes it. */
export const followScopes = "scope";
/** The value of a session or a scope that is not sandboxed. */
export const notSandboxed = "off";

/** Where the sandbox of a session comes from, said after its profile. */
export const sandboxSources: Readonly<Record<string, MessageKey>> = Object.freeze({
  session: "Chosen for this session",
  model: "From its model",
  provider: "From its provider",
  project: "From its project",
  space: "From its space",
  all_sessions: "From the setting of every session",
});

/** What a kind of scope is called in the list of the places that choose a sandbox. */
export const sandboxScopeKinds: Readonly<Record<SandboxScopeKind, MessageKey>> = Object.freeze({
  provider: "Provider", model: "Model", space: "Space", project: "Project",
});

/** The sandbox of a session as the chip shows it: its profile (null when it is not sandboxed), where it comes from, and its problem. */
export type SessionSandboxSummary = Readonly<{ profile: string | null; source: string; problem: string | null }>;

/** The summary of an answer of `session`, `setSession` or `draft`; null when it could not be read. */
export function sessionSandboxSummary(response: SandboxSessionResponse | null | undefined): SessionSandboxSummary | null {
  if (!response || response.status !== "ok") return null;
  return { profile: response.profile, source: response.source, problem: response.problem };
}

/**
 * The sandbox of a session as its composer keeps it: the last answer of the host, and what the settings give, which
 * is known while the session follows them (undefined before that).
 */
export type SessionSandboxState = Readonly<{ key: string; reply: SandboxSessionResponse; inherited: string | null | undefined }>;

/** The state after an answer for the session `key`: once the session has a choice of its own, what the settings gave is kept. */
export function nextSessionSandbox(current: SessionSandboxState | null, key: string, reply: SandboxSessionResponse): SessionSandboxState {
  const inherited = reply.own === null ? reply.profile : current?.key === key ? current.inherited : undefined;
  return { key, reply, inherited };
}

/** The profiles a session can choose, without `off`, which the controls offer apart. */
export function sandboxProfileChoices(choices: readonly string[]): readonly string[] {
  return choices.filter(choice => choice.toLowerCase() !== notSandboxed);
}

/** What a scope chooses (a profile or `off`), or null when it chooses nothing. */
export function scopeSandbox(scopes: readonly SandboxScopeItem[], kind: SandboxScopeKind, id: string, modelId: string | null = null): string | null {
  const same = (left: string | null, right: string | null) => (left ?? "").toLowerCase() === (right ?? "").toLowerCase();
  return scopes.find(scope => scope.kind === kind && same(scope.id, id) && (kind !== "model" || same(scope.modelId, modelId)))?.profile ?? null;
}

/** What a profile gives once its base is applied: the lists of its base and its own, its own choices over those of its base. */
export type ResolvedSandboxProfile = Readonly<{ network: string; outside: string; hosts: readonly string[]; write: readonly string[]; read: readonly string[];
  deny: readonly string[]; localPorts: readonly number[]; mcpServers: readonly string[] }>;

/**
 * What the profile `name` gives, from the profiles the host lists (those of CodeAlta resolved, those of the user as
 * written), as the host resolves it; null when it does not resolve (a loop, a missing base).
 */
export function resolveSandboxProfile(name: string, profiles: readonly SandboxProfileItem[], visiting: readonly string[] = []): ResolvedSandboxProfile | null {
  const profile = profiles.find(item => item.name.toLowerCase() === name.toLowerCase());
  if (!profile || visiting.includes(profile.name.toLowerCase())) return null;
  const own = { hosts: profile.hosts, write: profile.write, read: profile.read, deny: profile.deny, localPorts: profile.localPorts, mcpServers: profile.mcpServers };
  if (profile.builtIn || !profile.extends) return { network: profile.network ?? "off", outside: profile.outside ?? "ask", ...own };
  const base = resolveSandboxProfile(profile.extends, profiles, [...visiting, profile.name.toLowerCase()]);
  if (!base) return null;
  const union = <T>(left: readonly T[], right: readonly T[]) => [...new Set([...left, ...right])];
  return { network: profile.network ?? base.network, outside: profile.outside ?? base.outside, hosts: union(base.hosts, own.hosts), write: union(base.write, own.write),
    read: union(base.read, own.read), deny: union(base.deny, own.deny), localPorts: union(base.localPorts, own.localPorts), mcpServers: union(base.mcpServers, own.mcpServers) };
}

/** The models of a provider that choose a sandbox of their own, in the order the host lists them. */
export function modelSandboxes(scopes: readonly SandboxScopeItem[], providerKey: string): readonly Readonly<{ modelId: string; profile: string }>[] {
  return scopes.filter(scope => scope.kind === "model" && scope.id.toLowerCase() === providerKey.toLowerCase() && scope.modelId)
    .map(scope => ({ modelId: scope.modelId!, profile: scope.profile }));
}
