// The tabs of Settings > Secrets and environment: their order, their titles, what each says of itself in a few words,
// the link and the settings that open one, and what the page says of a secret and of the environment of a project.
import type { secrets, SecretsList, SecretView } from "#neoastra";
import type { MessageKey } from "../localization";
import { stateLabel } from "./credentials";

/** The tabs of the page, in their order. */
export const secretsTabs = Object.freeze(["overview", "secrets", "variables", "environment", "vaults", "signIns", "activity"] as const);
/** A tab of the page. */
export type SecretsTab = typeof secretsTabs[number];
/** The title of each tab. */
export const secretsTabTitles: Readonly<Record<SecretsTab, MessageKey>> = Object.freeze({
  overview: "Overview", secrets: "Secrets", variables: "Variables", environment: "Environment", vaults: "Vaults", signIns: "Sign-ins", activity: "Activity",
});

/** The setting of the catalog that stands for each tab (`builtInSettings.json`): `alta settings open <id>` opens the tab. */
export const secretsTabSettings: Readonly<Record<string, SecretsTab>> = Object.freeze({
  "secrets.list": "secrets", "secrets.variables": "variables", "secrets.environment": "environment", "secrets.vaults": "vaults",
  "secrets.sign_ins": "signIns", "secrets.activity": "activity",
});

/** The setting that stands for a tab, which glows when the tab is opened by it; null for the overview. */
export function secretsTabSetting(tab: SecretsTab): string | null {
  return Object.entries(secretsTabSettings).find(([, value]) => value === tab)?.[0] ?? null;
}

/** The name of the Settings page that opens a tab: `secrets/<tab>`. */
export function secretsPageLink(tab: SecretsTab): string {
  return `secrets/${tab}`;
}

/**
 * The tab a Settings page name and a setting open: `secrets/<tab>`, or `secrets` with one of the settings of the tabs.
 * Undefined for a page that is not this one; null for this page without a tab, which opens where it was left.
 */
export function readSecretsPageLink(page: string, setting: string | null = null): SecretsTab | null | undefined {
  const [root, tab, ...rest] = page.split("/");
  if (root !== "secrets" || rest.length > 0) return undefined;
  if (tab !== undefined) return (secretsTabs as readonly string[]).includes(tab) ? tab as SecretsTab : undefined;
  return setting ? secretsTabSettings[setting] ?? null : null;
}

/**
 * A file of a project as the editor of the project names it: its path under the folder of the project, with forward
 * slashes; the path as it is for a file outside that folder.
 */
export function projectRelativePath(projectPath: string, file: string): string {
  const slashes = (path: string) => path.replaceAll("\\", "/");
  const root = slashes(projectPath).replace(/\/+$/, "");
  const path = slashes(file);
  return path.toLowerCase().startsWith(`${root.toLowerCase()}/`) ? path.slice(root.length + 1) : path;
}

const lastTabKey = "codealta.secrets.tab";

/** The tab the page was left on, where the window keeps it; the overview otherwise. */
export function readLastSecretsTab(storage: Pick<Storage, "getItem"> | null = typeof localStorage === "undefined" ? null : localStorage): SecretsTab {
  try {
    const kept = storage?.getItem(lastTabKey);
    return kept && (secretsTabs as readonly string[]).includes(kept) ? kept as SecretsTab : "overview";
  } catch { return "overview"; }
}

/** Keeps the tab the page is on, for the next time it opens. */
export function keepLastSecretsTab(tab: SecretsTab, storage: Pick<Storage, "setItem"> | null = typeof localStorage === "undefined" ? null : localStorage) {
  try { storage?.setItem(lastTabKey, tab); } catch { /* A window without storage opens on the overview. */ }
}

/** What the page reads of the environment of the projects and of the spaces. */
export type EnvironmentList = Awaited<ReturnType<typeof secrets.listEnvironment>>;
/** A project of the catalog, with the environment its configuration sets. */
export type EnvironmentProject = EnvironmentList["projects"][number];
/** A variable of the environment of a project. */
export type EnvironmentVariable = EnvironmentProject["variables"][number];
/** What keeps a value of the environment from being resolved on this computer. */
export type EnvironmentIssue = EnvironmentProject["issues"][number];
/** A space whose Identity sets CLI profiles. */
export type EnvironmentSpace = EnvironmentList["spaces"][number];
/** A project of the catalog, as a secret is limited to it. */
export type SecretProject = Readonly<{ id: string; name: string; icon?: string | null }>;

type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

/** The projects of the catalog a listing gives; none for a machine of the fleet, which has no catalog. */
export function listedProjects(listing: Pick<SecretsList, "projects">): readonly SecretProject[] {
  return listing.projects ?? [];
}

/** The projects a secret is limited to; none when every project is given it. */
export function secretProjectIds(secret: Pick<SecretView, "projectIds">): readonly string[] {
  return secret.projectIds ?? [];
}

/** The names of the projects a secret is limited to, in the order of the catalog; a project the catalog no longer has keeps its id. */
export function secretProjectNames(secret: Pick<SecretView, "projectIds">, projects: readonly SecretProject[]): string[] {
  const ids = secretProjectIds(secret);
  const known = projects.filter(project => ids.includes(project.id)).map(project => project.name);
  const unknown = ids.filter(id => !projects.some(project => project.id === id));
  return [...known, ...unknown];
}

/** Where a secret is kept, in words: this computer with its version, or its vault and its item. */
export function secretSourceText(secret: SecretView, t: Translate): string {
  return secret.provider ? `${secret.providerName ?? secret.provider}${secret.label ? ` › ${secret.label}` : ""}`
    : `${t("This computer")} · v${secret.version}`;
}

/** What a secret says of itself to the filter of its table: its name, where it is kept, its state and its projects. */
export function secretSearchText(secret: SecretView, projects: readonly SecretProject[], t: Translate): string {
  return [secret.name, secretSourceText(secret, t), secret.state === "ready" ? "" : t(stateLabel(secret.state).label), ...secretProjectNames(secret, projects)].join(" ");
}

/** The projects that set an environment: variables of their own, or CLI profiles. The others are only offered to be given one. */
export function projectsWithEnvironment(environment: Pick<EnvironmentList, "projects"> | null): readonly EnvironmentProject[] {
  return (environment?.projects ?? []).filter(project => project.variables.length > 0 || project.profiles.length > 0);
}

/** Every issue of a project: those of its variables, and those of its profiles. One issue of the same kind and name counts once. */
export function projectIssues(project: EnvironmentProject): readonly EnvironmentIssue[] {
  const seen = new Set<string>();
  return [...project.variables.flatMap(variable => variable.issues), ...project.issues].filter(issue => {
    const key = `${issue.kind}\n${issue.name}\n${issue.state}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** What keeps a value from being resolved on this computer, in words: "ACME_GH_TOKEN is not set". */
export function issueText(issue: EnvironmentIssue, t: Translate): string {
  switch (issue.state) {
    case "not_set": return t("{name} is not set", { name: issue.name });
    case "locked": return t("{name} is in a locked vault", { name: issue.name });
    case "restricted": return t("{name} is limited to other projects", { name: issue.name });
    default: return issue.message ?? t("{name} is not available", { name: issue.name });
  }
}

/** The state of the environment of a project on this computer, in a few words: all set, or what is missing. */
export function environmentStateText(project: EnvironmentProject, t: Translate): string {
  const issues = projectIssues(project);
  if (issues.length === 0) return t("All set");
  const missing = issues.filter(issue => issue.kind === "secret" && issue.state === "not_set").length;
  if (missing === issues.length) return missing === 1 ? t("1 secret not set") : t("{count} secrets not set", { count: missing });
  return issues.length === 1 ? issueText(issues[0], t) : t("{count} values not available", { count: issues.length });
}

/** The secrets the projects name that are not set on this computer, each once, in the order they are met. */
export function missingSecrets(environment: Pick<EnvironmentList, "projects"> | null): string[] {
  const names = projectsWithEnvironment(environment).flatMap(project => projectIssues(project))
    .filter(issue => issue.kind === "secret" && issue.state === "not_set").map(issue => issue.name);
  return [...new Set(names)];
}

/** The secrets that cannot be given now: a locked vault, an item that is gone. */
export function secretsNotReady(listing: Pick<SecretsList, "secrets">): readonly SecretView[] {
  return listing.secrets.filter(secret => secret.state !== "ready");
}

/**
 * What a tab says of itself under its title, in a few words: "12 · 1 locked", "3", "2 projects", "1Password", "5 recent".
 * Null for a tab with nothing to say.
 */
export function secretsTabStatus(tab: SecretsTab, listing: SecretsList, environment: EnvironmentList | null, t: Translate): string | null {
  switch (tab) {
    case "overview": return null;
    case "secrets": {
      const locked = listing.secrets.filter(secret => secret.state === "locked").length;
      const other = secretsNotReady(listing).length - locked;
      return [String(listing.secrets.length), locked > 0 ? t("{count} locked", { count: locked }) : null, other > 0 ? t("{count} not ready", { count: other }) : null]
        .filter(Boolean).join(" · ");
    }
    case "variables": return String(listing.variables.length);
    case "environment": {
      const count = projectsWithEnvironment(environment).length;
      return count === 1 ? t("1 project") : t("{count} projects", { count });
    }
    case "vaults": return listing.providers.length === 0 ? t("This computer") : listing.providers.length <= 2
      ? listing.providers.map(provider => provider.displayName).join(", ") : String(listing.providers.length);
    case "signIns": return String(listing.signIns.length);
    case "activity": return listing.uses.length > 0 ? t("{count} recent", { count: listing.uses.length }) : null;
  }
}

/** The tabs a listing offers: a host without a credential broker has neither vaults nor sign-ins, one that does not read the projects no environment. */
export function offeredSecretsTabs(listing: Pick<SecretsList, "types">, environment: EnvironmentList | null): readonly SecretsTab[] {
  return secretsTabs.filter(tab => tab === "vaults" || tab === "signIns" ? listing.types.length > 0 : tab === "environment" ? environment !== null : true);
}
