import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SkillsEntry, SkillsFolder } from "#neoastra";
import { locales, translate } from "./localization";
import { SkillFolderList, SkillRows, spacelessSkillSource } from "./SkillSettings";
import { skillFolderNotice, skillFolderScopes, skillFolderStateLabel } from "./skillFolders";
import { skillSourceLabels } from "./skillSources";
import { skillReadOnly } from "./fileTabs";
import { ShellLanguageContext } from "./shellLanguage";
import { readAssignments, readSpaces } from "./spaces/spaces";

const never = () => assert.fail("rendering must not act");
const folder = (scope: string, path: string, state = "ready", skills = 2): SkillsFolder => ({ scope, path, fullPath: path, state, skills });

function render(folders: readonly SkillsFolder[], change: { locale?: typeof locales[number]; project?: boolean; showSpace?: boolean } = {}) {
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(SkillFolderList, { folders, space: { id: "work", name: "Work" }, project: change.project === false ? null : { id: "p1", name: "app" },
      showSpace: change.showSpace ?? true, disabled: false, onAdd: never, onRemove: never })));
}

test("the folders of skills are listed by where they are read, the project first, then the space, then every space, in every language", () => {
  for (const locale of locales) {
    const html = render([folder("Global", "/libs/all"), folder("Space", "/libs/work", "ready", 1), folder("Project", "/libs/app", "missing", 0)], { locale });
    const rows = html.split('role="listitem"').slice(1);
    assert.deepEqual(rows.map(row => /<code title="([^"]*)"/.exec(row)?.[1]), ["/libs/app", "/libs/work", "/libs/all"]);
    assert.ok(rows[0].includes(translate(locale, "Project: {name}", { name: "app" })) && rows[0].includes(translate(locale, "Not found")), rows[0]);
    assert.ok(rows[1].includes(translate(locale, "Space: {name}", { name: "Work" })) && rows[1].includes(translate(locale, "1 skill")), rows[1]);
    assert.ok(rows[2].includes(translate(locale, "Every space")) && rows[2].includes(translate(locale, "{count} skills", { count: 2 })), rows[2]);
    // Each row is taken out of the list with its own button, which says which folder it removes.
    for (const [index, path] of ["/libs/app", "/libs/work", "/libs/all"].entries())
      assert.ok(rows[index].includes(`aria-label="${translate(locale, "Remove from the list")}: ${path}"`), rows[index]);
    assert.ok(html.includes(`aria-label="${translate(locale, "Add a folder of skills")}"`), html);
  }
  assert.ok(render([]).includes("No folder added."));
});

test("a folder is added for every space, for the space when the window has several, and for the project when one is selected", () => {
  assert.deepEqual(skillFolderScopes(true, true), ["Global", "Space", "Project"]);
  assert.deepEqual(skillFolderScopes(false, true), ["Global", "Project"]);
  assert.deepEqual(skillFolderScopes(false, false), ["Global"]);
});

test("a folder from which no skill is read says why", () => {
  assert.equal(skillFolderStateLabel("ready"), null);
  assert.equal(skillFolderStateLabel("missing")?.label, "Not found");
  assert.equal(skillFolderStateLabel("unreadable")?.label, "Cannot be read");
  assert.equal(skillFolderStateLabel("invalid")?.label, "Not read");
  for (const locale of locales) for (const state of ["missing", "unreadable", "invalid"]) {
    const label = skillFolderStateLabel(state)!;
    assert.notEqual(translate(locale, label.title), "", `${locale} ${state}`);
  }
});

test("the refusals of the host are said in the words of the page; the others are left to every page", () => {
  assert.equal(skillFolderNotice("folder_missing")?.key, "The folder does not exist.");
  assert.equal(skillFolderNotice("folder_refused", "Home")?.key, "The home folder cannot be a folder of skills: choose the folder that holds the skills.");
  assert.equal(skillFolderNotice("folder_refused", "Disk")?.key, "A whole disk cannot be a folder of skills.");
  assert.equal(skillFolderNotice("folder_refused", "CodeAlta")?.key, "The folder of CodeAlta cannot be a folder of skills: its skills are already read.");
  assert.equal(skillFolderNotice("folder_refused", "NotFullPath")?.key, "Give the full path of the folder.");
  assert.equal(skillFolderNotice("folder_listed")?.key, "This folder is already in the list.");
  assert.equal(skillFolderNotice("folder_limit")?.key, "A list holds at most 32 folders of skills.");
  assert.equal(skillFolderNotice("conflict"), undefined);
});

test("a skill of an added folder says where it comes from, is edited in the code editor, and one of a space shows no spaces of its own", () => {
  assert.deepEqual(["UserFolder", "SpaceFolder", "ProjectFolder"].map(source => skillSourceLabels[source]), ["User", "Space", "Project"]);
  for (const source of ["UserFolder", "SpaceFolder", "ProjectFolder"]) assert.equal(skillReadOnly(source), false, source);
  assert.equal(spacelessSkillSource("SpaceFolder"), true);
  assert.equal(spacelessSkillSource("ProjectFolder"), true);
  assert.equal(spacelessSkillSource("UserFolder"), false);

  const spaces = readSpaces([{ id: "default", name: "Default", description: null, icon: null, color: null, isDefault: true, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] },
    { id: "work", name: "Work", description: null, icon: "briefcase", color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] }]);
  const scope = { spaces, assignments: readAssignments({ status: "ok", providers: [], mcpServers: [], plugins: [], skills: [], prompts: [] }), assign: never };
  const entry = (name: string, source: string): SkillsEntry => ({ id: `${source}-${name}`, name, title: name, description: "What it is for.", source,
    scope: source === "ProjectFolder" ? "Project" : "User", enabledGlobal: true, enabledProject: true, enabled: true, valid: true, shadowed: false, trusted: true,
    folder: null, path: `/skills/${name}` });
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillRows, { skills: [entry("everywhere", "UserFolder"), entry("work-only", "SpaceFolder")], spaceScope: scope, empty: "No skills were found.",
      selected: undefined, disabled: false, onSelect: never, onToggle: never })));
  const rows = html.split('<div class="bp6-card').slice(1);
  assert.ok(rows[0].includes("space-scope-button"), rows[0]);
  assert.ok(!rows[1].includes("space-scope-button"), rows[1]);
  assert.ok(rows[1].includes(">Space<"), rows[1]);
});
