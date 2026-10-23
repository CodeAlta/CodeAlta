import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { SkillsDetailResponse, SkillsEntry, SkillsMcpEntry } from "#neoastra";
import { formatFileSize } from "./editor/editorDocuments";
import { locales, translate } from "./localization";
import { McpSkillDetail, McpSkillRows, mcpApprovalLabels, mcpSkillKey, SkillDetail, SkillRows, skillRemovable, skillToggleable } from "./SkillSettings";
import { ShellLanguageContext } from "./shellLanguage";
import { readAssignments, readSpaces } from "./spaces/spaces";

const never = () => assert.fail("rendering must not act");
const entry = (name: string, source: string): SkillsEntry => ({ id: `${source}-${name}`, name, title: name, description: "What it is for.", source,
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

test("invalid or missing names cannot write name-based enablement, but keep their selection and removal", () => {
  for (const name of ["", " ", "Not A Slug", "../skill", "-skill", "skill-", "two--dashes", "a".repeat(65), "name:tool", "bad_name"])
    assert.equal(skillToggleable(name), false, name);
  for (const name of ["valid-name", "UPPERCASE", "écrire", "计划", "١-٢", " spaced ", "a".repeat(64)])
    assert.equal(skillToggleable(name), true, name);
  const skills = [
    { ...entry("", "ProjectAlta"), id: "first-file", title: "Broken first", valid: false, folder: null },
    { ...entry("", "ProjectAlta"), id: "second-file", title: "Broken second", valid: false, folder: null },
  ];
  const rows = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillRows, { skills, empty: "No skills were found.", selected: skills[1], disabled: false, onSelect: never, onToggle: never, onDelete: never })))
    .split('<div class="bp6-card').slice(1);
  assert.equal(rows.length, 2);
  assert.ok(!rows[0].includes("aria-current") && rows[1].includes('aria-current="true"'));
  for (const [index, title] of ["Broken first", "Broken second"].entries()) {
    assert.match(rows[index], /<input[^>]*disabled=""/);
    assert.ok(rows[index].includes(`aria-label="Enable ${title}"`));
    assert.ok(rows[index].includes(`aria-label="Remove ${title}"`));
  }
});

test("a skill of the user shows its spaces beside its switch; a skill of a project follows its project and shows none", () => {
  const spaces = readSpaces([{ id: "default", name: "Default", description: null, icon: null, color: null, isDefault: true, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] },
    { id: "work", name: "Work", description: null, icon: "briefcase", color: null, isDefault: false, projectIds: [], file: null, agentPrompt: null, disabledTools: [], ownTiers: false, identityEmail: null, identityParts: [] }]);
  const assignments = readAssignments({ status: "ok", providers: [], mcpServers: [], plugins: [], skills: [{ key: "terraform", spaces: ["work"] }], prompts: [] });
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillRows, { skills: [entry("terraform", "UserAlta"), entry("notes", "UserCommon"), entry("local", "ProjectAlta")], empty: "No skills were found.",
      selected: undefined, disabled: false, onSelect: never, onToggle: never, spaceScope: { spaces, assignments, assign: never } })));
  assert.match(html, /aria-label="Spaces of terraform"/);
  assert.match(html, /class="space-scope-name">Only: Work</);
  assert.match(html, /aria-label="Spaces of notes"[\s\S]*All spaces/);
  assert.doesNotMatch(html, /Spaces of local/);
  // With the default space alone there is nothing to say.
  const alone = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(SkillRows, { skills: [entry("terraform", "UserAlta")], empty: "No skills were found.", selected: undefined, disabled: false, onSelect: never, onToggle: never, spaceScope: null })));
  assert.doesNotMatch(alone, /space-scope/);
});

// The text of a translation as static markup writes it.
const text = (...args: Parameters<typeof translate>) => translate(...args).replace(/&/g, "&amp;").replace(/'/g, "&#x27;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const mcpEntry = (server: string, name: string, change: Partial<SkillsMcpEntry> = {}): SkillsMcpEntry => ({ server, name, description: "What it is for.",
  uri: `skill://${server}/${name}/SKILL.md`, enabled: true, namedLikeLocal: false, approval: "not_approved", dynamic: false, fileCount: 2, totalBytes: 3072,
  files: [{ path: "SKILL.md", size: 1024 }, { path: "scripts/run.ps1", size: 2048 }],
  frontmatter: [{ name: "name", value: name, ignored: false }, { name: "description", value: "What it is for.", ignored: false },
    { name: "license", value: "MIT", ignored: false }, { name: "allowed-tools", value: "Bash Read", ignored: true }], ...change });
const inLocale = (locale: typeof locales[number], element: Parameters<typeof renderToStaticMarkup>[0]) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } }, element));

test("the skills of the MCP servers are listed by server, each with the tag of its server, its mark and its switch, in every language", () => {
  const servers = [
    { server: "billing", listedAt: "2026-10-23T10:00:00Z", problems: [], skills: [mcpEntry("billing", "refunds"), mcpEntry("billing", "release-notes", { namedLikeLocal: true })] },
    { server: "docs", listedAt: "2026-10-23T10:00:00Z", problems: ["The frontmatter of skill://docs/broken/SKILL.md has no name."], skills: [mcpEntry("docs", "search", { enabled: false })] }];
  for (const locale of locales) {
    const html = inLocale(locale, createElement(McpSkillRows, { servers, selected: mcpSkillKey(servers[0].skills[1]), disabled: false, onSelect: never, onToggle: never }));
    assert.deepEqual([...html.matchAll(/<h3 class="settings-editor-group">([^<]*)<\/h3>/g)].map(match => match[1]),
      ["billing", "docs"].map(server => text(locale, "From the MCP server {server}", { server })), html);
    assert.ok(html.includes(`aria-label="${text(locale, "Skills from MCP servers")}"`), html);
    assert.ok(html.includes("The frontmatter of skill://docs/broken/SKILL.md has no name."), html);
    const rows = html.split('data-mcp-skill="').slice(1);
    assert.deepEqual(rows.map(row => row.slice(0, row.indexOf('"'))), ["mcp:billing:skill://billing/refunds/SKILL.md", "mcp:billing:skill://billing/release-notes/SKILL.md",
      "mcp:docs:skill://docs/search/SKILL.md"]);
    for (const [index, [server, name]] of [["billing", "refunds"], ["billing", "release-notes"], ["docs", "search"]].entries()) {
      assert.ok(rows[index].includes(text(locale, "MCP · {server}", { server })), rows[index]);
      assert.ok(rows[index].includes(`aria-label="${text(locale, "Enable {name} from {server}", { name, server })}"`), rows[index]);
      // Nothing of a skill of a server is edited or removed here.
      assert.ok(!rows[index].includes("<button"), rows[index]);
    }
    assert.deepEqual(rows.map(row => row.includes(text(locale, "Named like a local skill"))), [false, true, false]);
    assert.deepEqual(rows.map(row => row.includes('aria-current="true"')), [false, true, false]);
    assert.deepEqual(rows.map(row => /<input[^>]*checked=""/.test(row.slice(0, row.indexOf("</label>")))), [true, true, false]);
  }
  assert.equal(inLocale("en", createElement(McpSkillRows, { servers: [], selected: null, disabled: false, onSelect: never, onToggle: never })), "", "No server, no list.");
  const disabled = inLocale("en", createElement(McpSkillRows, { servers, selected: null, disabled: true, onSelect: never, onToggle: never }));
  assert.equal([...disabled.matchAll(/<input[^>]*disabled=""/g)].length, 3, "a write in progress disables the switches");
});

test("the details of a skill of an MCP server say its fields, allowed-tools ignored, its files and its approval, in every language", () => {
  for (const locale of locales) {
    for (const approval of ["approved", "not_approved", "changed", "once_only"]) {
      const html = inLocale(locale, createElement(McpSkillDetail, { skill: mcpEntry("billing", "refunds", { approval }), onForget: never, readContent: never }));
      assert.ok(html.includes(`data-mcp-approval="${approval}">${text(locale, mcpApprovalLabels[approval])}<`), html);
      // An approval is forgotten only where there is one.
      assert.equal(html.includes(`>${text(locale, "Forget approval")}<`), approval === "approved" || approval === "changed", `${locale} ${approval}`);
    }
    const html = inLocale(locale, createElement(McpSkillDetail, { skill: mcpEntry("billing", "refunds"), readContent: never }));
    assert.ok(html.includes(`aria-label="${text(locale, "Details for {name}", { name: "refunds" })}"`) && html.includes(text(locale, "MCP · {server}", { server: "billing" })), html);
    assert.ok(html.includes(`>${text(locale, "Show content")}<`), html);
    const fields = [...html.matchAll(/<div data-mcp-field="([^"]*)"><dt>[^<]*<\/dt><dd>([\s\S]*?)<\/dd><\/div>/g)].map(match => [match[1], match[2].includes(text(locale, "Ignored"))]);
    assert.deepEqual(fields, [["license", false], ["allowed-tools", true]], "name and description are said above; only allowed-tools is ignored");
    assert.ok(html.includes(text(locale, "{count} files, {size}", { count: 2, size: formatFileSize(3072, locale) })), html);
    const single = inLocale(locale, createElement(McpSkillDetail, { skill: mcpEntry("billing", "one", { fileCount: 1, totalBytes: 1024, files: [{ path: "SKILL.md", size: 1024 }] }) }));
    assert.ok(single.includes(text(locale, "1 file, {size}", { size: formatFileSize(1024, locale) })), single);
    assert.ok(html.includes(`data-mcp-file="scripts/run.ps1"><code>scripts/run.ps1</code> <span class="bp6-text-muted">${formatFileSize(2048, locale)}</span>`), html);
    // A skill its server generates has no files to list.
    const dynamic = inLocale(locale, createElement(McpSkillDetail, { skill: mcpEntry("docs", "search", { dynamic: true, approval: "once_only", fileCount: 0, totalBytes: 0, files: [] }) }));
    assert.ok(dynamic.includes(text(locale, "Generated by its server")) && !dynamic.includes(`<dt>${text(locale, "Files")}</dt>`), dynamic);
  }
  // Without a host to read from or to write to, the details have no button.
  assert.ok(!inLocale("en", createElement(McpSkillDetail, { skill: mcpEntry("billing", "refunds", { approval: "approved" }) })).includes("<button"));
});
