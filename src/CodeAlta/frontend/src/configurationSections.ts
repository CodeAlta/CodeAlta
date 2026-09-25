export type ConfigurationScope = "all" | "general" | "agent" | "extensions";
export type ConfigurationSectionId = "appearance" | "logs" | "providers" | "prompts" | "skills" | "plugins" | "about";

type ConfigurationSection = Readonly<{
  id: ConfigurationSectionId;
  scope: Exclude<ConfigurationScope, "all">;
  searchText: string;
}>;

const sections: readonly ConfigurationSection[] = [
  { id: "appearance", scope: "general", searchText: "appearance theme dark light display navigator project sort recent name desktop rail collapse visibility" },
  { id: "logs", scope: "general", searchText: "application logs diagnostic current process in memory refresh" },
  { id: "providers", scope: "general", searchText: "providers accounts models enabled default reasoning" },
  { id: "prompts", scope: "agent", searchText: "agent prompts instructions system" },
  { id: "skills", scope: "agent", searchText: "skills project global activation" },
  { id: "plugins", scope: "extensions", searchText: "plugins extensions contributions mcp tools" },
  { id: "about", scope: "general", searchText: "about version diagnostics help" },
];

export function visibleConfigurationSections(scope: ConfigurationScope, query: string): ConfigurationSectionId[] {
  const terms = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  return sections
    .filter(section => scope === "all" || section.scope === scope)
    .filter(section => terms.every(term => section.searchText.includes(term)))
    .map(section => section.id);
}
