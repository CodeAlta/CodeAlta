// The Plugins page of Settings in a frame of a chosen width, with a host that the fixture plays: what the page lists,
// where its files are, and what it asks the host to open, show or remove. With `?skills` in its address the fixture
// shows the Skills page instead, listed by a host that could not read a configuration file.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { plugins, PluginsEntry, PluginsProblem, projectFiles, skills, SkillsEntry } from "#neoastra";
import type { Locale } from "./localization";
import { PluginSettings } from "./PluginSettings";
import type { SettingsFilesApi } from "./settingsFiles";
import { ShellLanguageContext } from "./shellLanguage";
import { SkillSettings } from "./SkillSettings";

const home = "C:\\Users\\someone-with-a-long-name\\.alta\\plugins";
const work = "D:\\work\\a-project-with-a-long-name\\.alta\\plugins";
const entry = (id: string, scope: "Global" | "Project", change: Partial<PluginsEntry> = {}): PluginsEntry => ({
  id, name: id, description: `What ${id} does.`, kind: "Source", scope, enabledGlobal: null, enabledProject: null, enabled: true, state: "Enabled", runtime: "running",
  runtimeMessage: null, folder: scope === "Project" ? `plugin:project:p:${id}` : `plugin:global:${id}`, path: `${scope === "Project" ? work : home}\\${id}`,
  loadable: true, changed: false, errors: null, ...change });
const state = {
  calls: [] as { method: string; request: unknown }[], opened: 0, edited: [] as string[], copied: [] as string[], openStatus: "ok", deleteStatus: "ok",
  listed: [entry("commits", "Global"), entry("local", "Project")] as PluginsEntry[],
  problems: [{ kind: "config", path: "D:\\work\\a-project-with-a-long-name\\.alta\\config.toml", message: "(2,1): expected ]", scope: "Project" }] as PluginsProblem[],
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
const skill = (name: string, source: string): SkillsEntry => ({ name, title: name, description: `What ${name} is for.`, source, scope: source.startsWith("Project") ? "Project" : "User",
  enabledGlobal: true, enabledProject: true, enabled: true, valid: true, shadowed: false, trusted: true, folder: `skill:global:${source}:${name}`, path: `C:\\skills\\${name}` });
const skillsApi = {
  list: called("skills.list", () => ({ status: "ok", projectId: "p", skills: [skill("release-notes", "UserAlta"), skill("triage", "ProjectAlta")], omitted: 0,
    problems: [{ kind: "config", path: "C:\\Users\\someone\\.alta\\config.toml", message: "(1,8): expected ]", scope: "Global" }] })),
  detail: called("skills.detail", () => ({ status: "read_failed" })),
} as unknown as typeof skills;
const showsSkills = location.search === "?skills";
// The clipboard of a page that was not opened by the user refuses: the fixture keeps what was copied.
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { state.copied.push(text); } } });

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [width, setWidth] = useState(900);
  Object.assign(window, { settingsFilesFixture: { state, setLocale, setWidth } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <div className="settings-dialog-content" style={{ width, height: 700, overflow: "auto" }}>
      {showsSkills ? <SkillSettings epoch="epoch" project={{ id: "p", name: "a-project" }} api={skillsApi} filesApi={filesApi} reveal={reveal}
          onEdit={folder => state.edited.push(folder.id)} onOpenFile={() => { state.opened++; }} />
        : <PluginSettings epoch="epoch" project={{ id: "p", name: "a-project" }} api={api} filesApi={filesApi} reveal={reveal}
          onEdit={folder => state.edited.push(folder.id)} onOpenFile={() => { state.opened++; }} />}
    </div>
  </ShellLanguageContext.Provider>;
}
// The window runs under StrictMode: an effect that runs twice must list the files once more, and nothing else.
createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
