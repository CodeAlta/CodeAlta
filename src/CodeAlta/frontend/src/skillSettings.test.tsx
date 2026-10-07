import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SkillsDetailResponse, SkillsEntry } from "#neoastra";
import { locales, translate } from "./localization";
import { SkillDetail } from "./SkillSettings";
import { ShellLanguageContext } from "./shellLanguage";

const never = () => assert.fail("rendering must not act");
const entry = (name: string, source: string): SkillsEntry => ({ name, title: name, description: "What it is for.", source,
  scope: source.startsWith("Project") ? "Project" : source.startsWith("User") ? "User" : source, enabledGlobal: true, enabledProject: true, enabled: true, valid: true, shadowed: false, trusted: true });
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

test("a skill has no button while its details are read, when its folder has no id, and where no editor opens", () => {
  const skill = entry("release-notes", "UserAlta");
  const button = translate("en", "Edit {name}", { name: "release-notes" });
  assert.ok(render(skill, detail(skill)).includes(button));
  assert.ok(!render(skill, undefined).includes(button), "The details are not read yet.");
  assert.ok(!render(skill, detail(skill, { folder: null })).includes(button), "The host did not name the folder.");
  assert.ok(!render(skill, detail(skill, { skillRootPath: null })).includes(button));
  assert.ok(!render(skill, detail(skill), { edit: false }).includes(button), "A page without an owned host opens no editor.");
});
