// The Plugins page of Settings in a frame of a chosen width, with a host that the fixture plays: what the page lists,
// where its files are, and what it asks the host to open, show or remove. With `?skills` in its address the fixture
// shows the Skills page instead, listed by a host that could not read a configuration file. `?skills=invalid`
// lists two unnamed skills and two with the same name: every file still has its own id. `?skills=many` lists 1,000 skills,
// with 30 more the host left out of the listing. `?prompts` shows the list of the Agent prompts page alone.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { CardList } from "@blueprintjs/core";
import type { AgentPromptEntry, plugins, PluginsEntry, PluginsProblem, projectFiles, skills, SkillsEntry } from "#neoastra";
import type { Locale } from "./localization";
import { PluginSettings } from "./PluginSettings";
import { PromptRows } from "./PromptRows";
import type { ResourceSpaces } from "./spaces/SpaceChips";
import { noAssignments, type Space } from "./spaces/spaces";
import type { SpaceOutcome } from "./spaces/spacesHub";
import type { SettingsFilesApi } from "./settingsFiles";
import { ShellLanguageContext } from "./shellLanguage";
import { SkillSettings } from "./SkillSettings";

const home = "C:\\Users\\someone-with-a-long-name\\.alta\\plugins";
const work = "D:\\work\\a-project-with-a-long-name\\.alta\\plugins";
const entry = (id: string, scope: "Global" | "Project", change: Partial<PluginsEntry> = {}): PluginsEntry => ({
  id, name: id, description: `What ${id} does.`, kind: "Source", scope, enabledGlobal: null, enabledProject: null, enabled: true, state: "Enabled", runtime: "running",
  runtimeMessage: null, folder: scope === "Project" ? `plugin:project:p:${id}` : `plugin:global:${id}`, path: `${scope === "Project" ? work : home}\\${id}`,
  loadable: true, changed: false, errors: null, ...change });
const skill = (id: string, name: string, source: string, change: Partial<SkillsEntry> = {}): SkillsEntry => ({ id, name, title: name, description: `What ${name} is for.`, source,
  scope: source.startsWith("Project") ? "Project" : "User", enabledGlobal: true, enabledProject: true, enabled: true, valid: true, shadowed: false, trusted: true,
  folder: `skill:global:${source}:${name}`, path: `C:\\skills\\${id}`, ...change });
const query = new URLSearchParams(location.search);
const invalidSkills = query.get("skills") === "invalid";
const manySkills = query.get("skills") === "many";
// The descriptions are of one or two lines, as they are in a real list, so the rows do not all have the same height.
const many = Array.from({ length: 1030 }, (_, index) => {
  const name = `skill-${String(index).padStart(4, "0")}`;
  return skill(name, name, index % 3 === 0 ? "ProjectAlta" : "UserAlta",
    { description: index % 2 ? `Does task ${index}.` : `Does task ${index}, with a description long enough to take a second line in the row of the list.` });
});
const state = {
  calls: [] as { method: string; request: unknown }[], opened: 0, edited: [] as string[], copied: [] as string[], openStatus: "ok", deleteStatus: "ok",
  listed: [entry("commits", "Global"), entry("local", "Project")] as PluginsEntry[],
  problems: [{ kind: "config", path: "D:\\work\\a-project-with-a-long-name\\.alta\\config.toml", message: "(2,1): expected ]", scope: "Project" }] as PluginsProblem[],
  skills: invalidSkills ? [
    skill("broken-first", "", "ProjectAlta", { title: "Broken first", valid: false, folder: null }),
    skill("broken-second", "", "ProjectAlta", { title: "Broken second", valid: false, folder: null }),
    skill("duplicate-first", "duplicate", "ProjectAlta", { title: "Duplicate first", folder: null }),
    skill("duplicate-second", "duplicate", "ProjectAlta", { title: "Duplicate second", folder: null }),
  ] : manySkills ? many : [skill("release-notes", "release-notes", "UserAlta"), skill("triage", "triage", "ProjectAlta")],
  holdDetails: false, pendingDetails: [] as { resolve: () => void }[],
};
const called = <T,>(method: string, answer: (request: any) => T) => async (request: unknown) => { state.calls.push({ method, request }); return answer(request); };
const api = {
  list: called("list", () => ({ status: "ok", projectId: "p", plugins: state.listed, omitted: 0, problems: state.problems })),
  setEnabled: called("setEnabled", () => ({ status: "ok", message: null, applied: true })),
  reload: called("reload", () => ({ status: "ok", message: null, applied: true })),
  create: called("create", () => ({ status: "ok", message: null, folder: null, path: null, name: null })),
  delete: called("delete", request => {
    if (state.deleteStatus === "ok") state.listed = state.listed.filter(value => !(value.id === request.id && value.scope === request.scope));
    return { status: state.deleteStatus, message: null, applied: true };
  }),
} as unknown as typeof plugins;
const filesApi = {
  list: called("files.list", () => ({ status: "ok", platform: "windows", locations: [
    { kind: "plugins", scope: "Global", id: null, path: home, folder: true, exists: true, canOpen: true, readOnly: false },
    { kind: "plugins", scope: "Project", id: null, path: work, folder: true, exists: false, canOpen: true, readOnly: false }] })),
  open: called("files.open", () => ({ status: state.openStatus })),
  reveal: called("files.reveal", () => ({ status: "ok" })),
} as unknown as SettingsFilesApi;
const reveal = called("reveal", () => ({ status: "ok", path: "" })) as unknown as typeof projectFiles.reveal;
// The skills as a host lists them when the configuration file of the user does not parse: every skill, and the file.
const skillsApi = {
  // The host lists at most 1,000 skills of the many: it says how many it left out.
  list: called("skills.list", request => ({ status: "ok", projectId: request.projectId, skills: manySkills ? state.skills.slice(0, 1000) : state.skills,
    omitted: manySkills ? Math.max(0, state.skills.length - 1000) : 0,
    problems: invalidSkills ? [] : [{ kind: "config", path: "C:\\Users\\someone\\.alta\\config.toml", message: "(1,8): expected ]", scope: "Global" }] })),
  detail: called("skills.detail", request => {
    if (!invalidSkills) return { status: "read_failed" };
    const value = state.skills.find(skill => skill.id === request.id && skill.source === request.source);
    const result = !value ? { status: "not_found" } : { status: "ok", name: value.name, source: value.source, skillFilePath: `${value.path}\\SKILL.md`, skillRootPath: value.path,
      sourceId: "project", shadowedBy: null, modelVisible: value.valid, license: null, compatibility: null, allowedTools: null,
      content: `# ${value.title}\n\nInstructions for ${value.id} in ${request.expectedEpoch}/${request.projectId}.`, contentTruncated: false,
      relatedFiles: [], diagnostics: value.valid ? [] : [{ severity: "Error", code: "frontmatter-invalid", message: `${value.title}: invalid YAML.` }], relatedFilesOmitted: 0, folder: value.folder };
    return state.holdDetails ? new Promise(resolve => state.pendingDetails.push({ resolve: () => resolve(result) })) : result;
  }),
  delete: called("skills.delete", request => {
    if (state.deleteStatus === "ok") state.skills = state.skills.filter(value => !(value.id === request.id && value.source === request.source));
    return { status: state.deleteStatus, changed: 1, message: null };
  }),
  setEnabled: called("skills.setEnabled", () => ({ status: "ok", changed: 1, message: null })),
  setAllEnabled: called("skills.setAllEnabled", request => {
    if (request.names.some((name: string) => !name)) return { status: "invalid", changed: 0, message: null };
    state.skills = state.skills.map(skill => request.names.includes(skill.name) ? { ...skill, enabled: request.enabled } : skill);
    return { status: "ok", changed: request.names.length, message: null };
  }),
  // As the host does: every skill whose name, title or description has the text, those it left out of the listing included.
  setMatchingEnabled: called("skills.setMatchingEnabled", request => {
    const text = (request.query ?? "").trim().toLowerCase();
    const matches = (value: SkillsEntry) => !!value.name && (!text || `${value.name} ${value.title} ${value.description}`.toLowerCase().includes(text));
    const names = new Set(state.skills.filter(matches).map(value => value.name));
    state.skills = state.skills.map(value => names.has(value.name) ? { ...value, enabled: request.enabled } : value);
    return { status: "ok", changed: names.size, message: null };
  }),
} as unknown as typeof skills;
const showsSkills = query.has("skills");
// The list of the Agent prompts page: built-in agent prompts with spaces and the button of the editor, and a system prompt.
const prompt = (id: string, name: string, kind: "Agent" | "System"): AgentPromptEntry => ({ id, name, kind, scope: "BuiltIn", description: `What ${name} does, in a sentence that does not fit on one line.`,
  inherits: [], append: kind === "Agent", readOnly: true, shadowed: false, shadowedByScope: null, systemPromptId: null });
const prompts = [prompt("default", "Implementation", "Agent"), prompt("plan", "Planning", "Agent"), prompt("default", "default", "System")];
const fixtureSpace = (id: string, name: string): Space => ({ id, name, description: null, icon: null, color: null, isDefault: id === "default", projectIds: [],
  file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] });
const promptSpaces: ResourceSpaces = { spaces: [fixtureSpace("default", "Default"), fixtureSpace("work", "Work")], assignments: noAssignments,
  assign: async (): Promise<SpaceOutcome> => ({ ok: true, status: "ok", message: null, space: null }) };
function PromptList() {
  return <div className="prompt-settings"><div className="settings-editor-layout">
    <CardList compact className="settings-editor-list">
      {(["Agent", "System"] as const).map(kind => <PromptRows key={kind} spaceScope={promptSpaces} prompts={prompts.filter(value => value.kind === kind)}
        title={kind === "Agent" ? "Agent prompts" : "System prompts"} selected={null} onSelect={() => {}} onOpen={() => {}} />)}
    </CardList><div /></div></div>;
}
// The clipboard of a page that was not opened by the user refuses: the fixture keeps what was copied.
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { state.copied.push(text); } } });

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [width, setWidth] = useState(900);
  const [height, setHeight] = useState(700);
  const [epoch, setEpoch] = useState("epoch");
  const [projectId, setProjectId] = useState("p");
  Object.assign(window, { settingsFilesFixture: { state, setLocale, setWidth, setHeight, setEpoch, setProjectId } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="settings-dialog-content" style={{ width, height, overflow: "auto" }}>
      {query.has("prompts") ? <PromptList /> : showsSkills ? <SkillSettings epoch={epoch} project={{ id: projectId, name: "a-project" }} api={skillsApi} filesApi={filesApi} reveal={reveal}
          onEdit={folder => state.edited.push(folder.id)} onOpenFile={() => { state.opened++; }} />
        : <PluginSettings epoch="epoch" project={{ id: "p", name: "a-project" }} api={api} filesApi={filesApi} reveal={reveal}
          onEdit={folder => state.edited.push(folder.id)} onOpenFile={() => { state.opened++; }} />}
    </div>
  </ShellLanguageContext.Provider>;
}
// The window runs under StrictMode: an effect that runs twice must list the files once more, and nothing else.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
