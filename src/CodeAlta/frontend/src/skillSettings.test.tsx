import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SkillsDetailResponse, SkillsEntry } from "#neoastra";
import { locales, translate } from "./localization";
import { SkillDetail, SkillRows, skillRemovable } from "./SkillSettings";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const entry = (name: string, source: string): SkillsEntry => ({ name, title: name, description: "What it is for.", source,
  scope: source.startsWith("Project") ? "Project" : source.startsWith("User") ? "User" : source, enabledGlobal: true, enabledProject: true, enabled: true, valid: true, shadowed: false, trusted: true,
  folder: `skill:global:${source}:${name}`, path: `/skills/${name}` });
const detail = (skill: SkillsEntry, change: Partial<SkillsDetailResponse> = {}): SkillsDetailResponse => ({ status: "ok", name: skill.name, source: skill.source,
  skillFilePath: `/skills/${skill.name}/SKILL.md`, skillRootPath: `/skills/${skill.name}`, sourceId: "source", shadowedBy: null, modelVisible: true, license: null,
  // The instructions are Markdown, which only a browser renders: the text of the file is left out.
  compatibility: null, allowedTools: null, content: null, contentTruncated: false, relatedFiles: [{ category: "scripts", path: "scripts/run.ps1" }],
  diagnostics: [], relatedFilesOmitted: 0, folder: `skill:global:${skill.source}:${skill.name}`, ...change });
function render(skill: SkillsEntry, value: SkillsDetailResponse | undefined, change: { locale?: typeof locales[number]; edit?: boolean } = {}) {
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(SkillDetail, { skill, detail: value, failed: false, onEdit: change.edit === false ? undefined : never })));
}

test("the folder of a skill opens in the code editor: to be edited when it is the user's or a project's, to be read otherwise", () => {
  for (const locale of locales) {
    for (const source of ["UserAlta", "UserCommon", "ProjectAlta", "ProjectCommon"]) {
      const skill = entry("release-notes", source);
      const html = render(skill, detail(skill), { locale });
      assert.ok(html.includes(`aria-label="${translate(locale, "Edit {name}", { name: "release-notes" })}"`) && html.includes(`title="${translate(locale, "Edit in the code editor")}"`), html);
      assert.ok(html.includes(`>${translate(locale, "Edit")}<`) && !html.includes(translate(locale, "View files")), html);
    }
    for (const source of ["Builtin", "Plugin"]) {
      const skill = entry("codealta-plugins", source);
      const html = render(skill, detail(skill), { locale });
      assert.ok(html.includes(`aria-label="${translate(locale, "View the files of {name}", { name: "codealta-plugins" })}"`) && html.includes(`title="${translate(locale, "Open in the code editor")}"`), html);
      assert.ok(html.includes(`>${translate(locale, "View files")}<`) && !html.includes(`>${translate(locale, "Edit")}<`), html);
    }
  }
});

test("the details of a skill say where its folder and its SKILL.md are, with the buttons that open, copy and show them", () => {
  const skill = entry("release-notes", "UserAlta");
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillDetail, { skill, detail: detail(skill), failed: false, onEdit: never, platform: "windows", onReveal: never })));
  const rows = html.split('class="settings-file-location"').slice(1);
  assert.deepEqual(rows.map(row => /<code title="([^"]*)"/.exec(row)?.[1]), ["/skills/release-notes", "/skills/release-notes/SKILL.md"]);
  for (const row of rows) assert.deepEqual([...row.matchAll(/<button[^>]*title="([^"]*)"/g)].map(match => match[1]), ["Edit in the code editor", "Copy path", "Reveal in File Explorer"], row);
  assert.ok(html.includes("<dt>Folder</dt>") && html.includes("<dt>Skill file</dt>"), html);
  // A skill that ships with CodeAlta is read: its files are opened, not edited.
  const builtin = entry("codealta-plugins", "Builtin");
  assert.ok(render(builtin, detail(builtin)).includes('title="Open in the code editor"') && !render(builtin, detail(builtin)).includes('title="Edit in the code editor"'));
});

test("the row of a skill opens its folder in the code editor, as the row of a plugin does, in every language", () => {
  const skills = [entry("release-notes", "UserAlta"), entry("codealta-plugins", "Builtin"), { ...entry("odd", "ProjectAlta"), folder: null }];
  const rows = (change: { locale?: typeof locales[number]; edit?: boolean; disabled?: boolean } = {}) => {
    const locale = change.locale ?? "en";
    return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(SkillRows, { skills, empty: "No skills were found.", selected: skills[0], disabled: change.disabled ?? false, onSelect: never, onToggle: never,
        onEdit: change.edit === false ? undefined : never }))).split('<div class="bp6-card').slice(1);
  };
  for (const locale of locales) {
    const [mine, shipped, odd] = rows({ locale });
    assert.ok(mine.includes(`aria-label="${translate(locale, "Edit {name}", { name: "release-notes" })}"`) && mine.includes(`title="${translate(locale, "Edit in the code editor")}"`), mine);
    assert.ok(shipped.includes(`aria-label="${translate(locale, "View the files of {name}", { name: "codealta-plugins" })}"`) && shipped.includes(`title="${translate(locale, "Open in the code editor")}"`), shipped);
    // A skill whose folder the host could not name has its switch and no button.
    assert.ok(!odd.includes("<button") && odd.includes(translate(locale, "Enable {name}", { name: "odd" })), odd);
  }
  assert.ok(rows({ edit: false }).every(row => !row.includes("<button")), "A page without an owned host opens no editor.");
  assert.ok(rows()[0].includes('aria-current="true"') && !rows()[1].includes("aria-current"));
  const empty = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillRows, { skills: [], empty: "No skill matches the filter.", selected: undefined, disabled: false, onSelect: never, onToggle: never })));
  assert.ok(empty.includes("No skill matches the filter."), empty);
});

test("a skill of the user or of a project is removed with a red button, in its row and in its details; any other skill is not", () => {
  assert.deepEqual(["UserAlta", "ProjectAlta", "UserCommon", "ProjectCommon", "UserCopilot", "ProjectCopilot", "Plugin", "Builtin"].filter(skillRemovable),
    ["UserAlta", "ProjectAlta", "UserCommon", "ProjectCommon"]);
  const skills = [entry("release-notes", "UserAlta"), entry("house", "ProjectCommon"), entry("codealta-plugins", "Builtin"), entry("copilot-skill", "UserCopilot"), entry("pack", "Plugin")];
  for (const locale of locales) {
    const rows = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(SkillRows, { skills, empty: "No skills were found.", selected: undefined, disabled: false, onSelect: never, onToggle: never, onEdit: never, onDelete: never })))
      .split('<div class="bp6-card').slice(1);
    for (const [index, name] of [[0, "release-notes"], [1, "house"]] as const) {
      assert.ok(rows[index].includes("bp6-intent-danger") && rows[index].includes(`aria-label="${translate(locale, "Remove {name}", { name })}"`), rows[index]);
    }
    for (const index of [2, 3, 4]) assert.ok(!rows[index].includes("bp6-intent-danger"), rows[index]);
    // The details have the same button, with its label.
    const mine = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(SkillDetail, { skill: skills[0], detail: detail(skills[0]), failed: false, onEdit: never, onDelete: never })));
    assert.match(mine, new RegExp(`<button[^>]*bp6-intent-danger[^>]*>.*?>${translate(locale, "Remove")}<`), mine);
    const shipped = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
      createElement(SkillDetail, { skill: skills[2], detail: detail(skills[2]), failed: false, onEdit: never, onDelete: never })));
    assert.ok(!shipped.includes("bp6-intent-danger"), shipped);
  }
  assert.ok(!render(skills[0], detail(skills[0])).includes("bp6-intent-danger"), "A page that removes nothing has no red button.");
});

test("a skill has no button while its details are read, when its folder has no id, and where no editor opens", () => {
  const skill = entry("release-notes", "UserAlta");
  const button = translate("en", "Edit {name}", { name: "release-notes" });
  assert.ok(render(skill, detail(skill)).includes(button));
  assert.ok(!render(skill, undefined).includes(button), "The details are not read yet.");
  assert.ok(!render(skill, detail(skill, { folder: null })).includes(button), "The host did not name the folder.");
  assert.ok(!render(skill, detail(skill, { skillRootPath: null })).includes(button));
  assert.ok(!render(skill, detail(skill), { edit: false }).includes(button), "A page without an owned host opens no editor.");
});
