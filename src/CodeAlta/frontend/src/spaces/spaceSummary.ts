// What a space sets up, in a few words: the line of its card in Settings > Spaces, and the status beside each section
// of its page. Also the link that opens the page of a space at one of its sections.
import type { MessageKey } from "../localization";
import type { ResourceLists } from "./spaceResources";
import type { ResourceAssignments, Space, SpaceResourceKind } from "./spaces";

/** The sections of the page of a space, in their order. */
export const spaceSections = Object.freeze(["general", "projects", "resources", "newSessions", "tools", "identity"] as const);
/** A section of the page of a space. */
export type SpaceSection = typeof spaceSections[number];
/** The title of each section. */
export const spaceSectionTitles: Readonly<Record<SpaceSection, MessageKey>> = Object.freeze({
  general: "General", projects: "Projects", resources: "Resources", newSessions: "New sessions", tools: "Tools", identity: "Identity",
});

/** Where Settings opens the page of a space, and which section of it. */
export type SpacePageTarget = Readonly<{ spaceId: string; section: SpaceSection }>;

const identifier = /^[a-z0-9][a-z0-9\-_.]{1,63}$/;

/** The name of the Settings page that opens a space at one of its sections: `spaces/<id>/<section>`. */
export function spacePageLink(spaceId: string, section: SpaceSection = "general"): string {
  return `spaces/${spaceId}/${section}`;
}

/** The space and the section a Settings page name opens; null for a name that is not one (`spaces/<id>[/<section>]`). */
export function readSpacePageLink(page: string): SpacePageTarget | null {
  const [root, spaceId, section, ...rest] = page.split("/");
  if (root !== "spaces" || !spaceId || !identifier.test(spaceId) || rest.length > 0) return null;
  if (section === undefined) return { spaceId, section: "general" };
  return (spaceSections as readonly string[]).includes(section) ? { spaceId, section: section as SpaceSection } : null;
}

type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

/**
 * The names of the resources of one kind that a space has by name: those assigned to it, alone or with other spaces.
 * A shared resource is in every space and is not counted. Once the resources are read, a name that no longer names
 * one is left out.
 */
export function ownResources(space: Space, assignments: ResourceAssignments, lists: ResourceLists, kind: SpaceResourceKind): readonly string[] {
  const keys = [...assignments[kind]].filter(([, ids]) => ids.includes(space.id)).map(([key]) => key);
  const listed = lists[kind];
  if (!listed) return keys;
  const byKey = new Map(listed.map(resource => [resource.key.toLowerCase(), resource.name]));
  return keys.flatMap(key => byKey.has(key) ? [byKey.get(key)!] : []);
}

const counted: readonly Readonly<{ kind: SpaceResourceKind; one: MessageKey; many: MessageKey }>[] = [
  { kind: "mcp", one: "{count} MCP", many: "{count} MCP" },
  { kind: "skill", one: "1 skill", many: "{count} skills" },
  { kind: "prompt", one: "1 agent prompt", many: "{count} agent prompts" },
  { kind: "plugin", one: "1 plugin", many: "{count} plugins" },
];

/**
 * The resources a space has by name, in a few words: its first provider (with how many more), then how many MCP
 * servers, skills, agent prompts and plugins. Empty when everything it has is shared.
 */
export function resourceSummary(space: Space, assignments: ResourceAssignments, lists: ResourceLists, t: Translate): readonly string[] {
  const providers = ownResources(space, assignments, lists, "provider");
  const parts: string[] = providers.length === 0 ? [] : [providers.length === 1 ? providers[0] : `${providers[0]} +${providers.length - 1}`];
  for (const { kind, one, many } of counted) {
    const count = ownResources(space, assignments, lists, kind).length;
    if (count > 0) parts.push(t(count === 1 ? one : many, { count }));
  }
  return parts;
}

/** The line of the card of a space: its projects, what it has by name, and the email it works under. */
export function spaceCardSummary(space: Space, projectCount: number, assignments: ResourceAssignments, lists: ResourceLists, t: Translate): readonly string[] {
  return [t(projectCount === 1 ? "1 project" : "{count} projects", { count: projectCount }), ...resourceSummary(space, assignments, lists, t),
    ...space.identityEmail ? [space.identityEmail] : []];
}

const identityNames = { git: "Git", github: "GitHub", jira: "Jira", cli: "CLI" } as const;

/** The status beside a section of the page of a space; null for a section that says nothing more than its title. */
export function spaceSectionStatus(section: SpaceSection, space: Space, projectCount: number, assignments: ResourceAssignments, lists: ResourceLists,
  t: Translate): string | null {
  switch (section) {
    case "general": return null;
    case "projects": return String(projectCount);
    case "resources": return resourceSummary(space, assignments, lists, t).join(" · ") || t("All shared");
    case "newSessions": {
      const parts = [...space.agentPrompt ? [space.agentPrompt] : [], ...space.ownTiers ? [t("Own model tiers")] : []];
      return parts.join(" · ") || t("Usual settings");
    }
    case "tools": return space.disabledTools.length > 0 ? t("{count} off", { count: space.disabledTools.length }) : t("None off");
    case "identity": return space.identityEmail ?? (space.identityParts.map(part => identityNames[part]).join(" · ") || t("Usual settings"));
  }
}
