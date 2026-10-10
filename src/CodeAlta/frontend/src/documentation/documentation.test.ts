import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import type { DocumentationMenuItem, DocumentationPageInfo } from "#neoastra";
import { documentationTab, emptyFileTabs, fileTabKey, isDocumentationTab, openFileTab, persistFileTabs, reconcileFileTabs, restoreFileTabs } from "../fileTabs";
import { commandDefinitions } from "../commandRegistry";
import { messages } from "../localization";
import { emptyHistory, headingAnchor, headingAnchors, highlightParts, menuIcon, menuNeighbors, menuNodes, menuOwner, menuTrail, moveHistory, pushHistory, resolveDocumentationLink, validAnchor } from "./documentation";
import { documentationMessages } from "./messages";

const item = (path: string, title: string, depth = 0, parent: string | null = null, icon: string | null = null): DocumentationMenuItem => ({ path, title, icon, depth, parent });
const menu = [item("readme.md", "User Guide", 0, null, "book"), item("sessions.md", "Sessions", 0, null, "diagram-3"), item("plugins/readme.md", "Plugins", 0, null, "puzzle"),
  item("plugins/git.md", "Git", 1, "plugins/readme.md"), item("plugins/mcp.md", "MCP", 1, "plugins/readme.md"), item("troubleshooting.md", "Troubleshooting", 0, null, "life-preserver")];
const pages: DocumentationPageInfo[] = [...menu.map(entry => ({ path: entry.path, title: entry.title })), { path: "orphan.md", title: "Orphan" }];

test("a heading is named as the site of the guide names it", () => {
  assert.equal(headingAnchor("Code editor"), "code-editor");
  assert.equal(headingAnchor("  Agent prompt, model and permissions "), "agent-prompt-model-and-permissions");
  assert.equal(headingAnchor("Changes (Desktop)"), "changes-desktop");
  assert.equal(headingAnchor("Read / write"), "read--write");
  assert.equal(headingAnchor("`alta` & the_tool-chain"), "alta--the_tool-chain");
  assert.equal(headingAnchor("Résumé 2"), "résumé-2");
  assert.equal(headingAnchor("!!!"), "");
  // A text that comes again takes a number, as the site does.
  assert.deepEqual(headingAnchors(["Setup", "Usage", "Setup", "Setup"]), ["setup", "usage", "setup-1", "setup-2"]);
});

test("every link of the shipped guide to a heading finds it with these addresses", () => {
  const root = fileURLToPath(new URL("../../../../../site/docs/", import.meta.url));
  const files: string[] = [];
  (function walk(folder: string, prefix: string) {
    for (const name of readdirSync(folder)) {
      if (statSync(join(folder, name)).isDirectory()) walk(join(folder, name), `${prefix}${name}/`);
      else if (name.endsWith(".md")) files.push(prefix + name);
    }
  })(root, "");
  assert.ok(files.length >= 20, `${files.length} pages`);
  const lines = (file: string) => {
    const kept: string[] = [];
    let fence: string | null = null;
    for (const line of readFileSync(join(root, file), "utf8").split(/\r?\n/)) {
      const mark = /^ {0,3}(`{3,}|~{3,})/.exec(line);
      if (mark) { fence = fence === null ? mark[1][0] : fence === mark[1][0] ? null : fence; continue; }
      if (fence === null) kept.push(line);
    }
    return kept;
  };
  // The headings as a reader sees them: without the marks of Markdown and the tags of the site.
  const plain = (text: string) => text.replace(/<[^>]+>/g, "").replace(/\[([^\]]*)\]\([^)]*\)/g, "$1").replace(/[`*]/g, "").trim();
  const anchors = new Map(files.map(file => [file, new Set(headingAnchors(lines(file).map(line => /^ {0,3}#{1,6}\s+(.*?)\s*#*\s*$/.exec(line)?.[1]).filter((text): text is string => text !== undefined).map(plain)))]));
  let checked = 0;
  for (const file of files) {
    const folder = file.includes("/") ? file.slice(0, file.lastIndexOf("/") + 1) : "";
    for (const match of lines(file).join("\n").matchAll(/\]\(([^)\s]+)\)|href="([^"]+)"/g)) {
      const target = match[1] ?? match[2];
      if (/^[a-z][a-z0-9+.-]*:/i.test(target) || !target.includes("#")) continue;
      const [path, anchor] = target.split("#");
      const site = /^\{\{site\.basepath\}\}\/docs\/(.*?)\/?$/.exec(path);
      let page = file;
      if (site) page = files.includes(`${site[1]}.md`) ? `${site[1]}.md` : `${site[1]}/readme.md`;
      else if (path) {
        const segments = folder.split("/").filter(Boolean);
        for (const segment of path.split("/")) { if (segment === "..") segments.pop(); else if (segment !== ".") segments.push(segment); }
        page = segments.join("/");
      }
      assert.ok(anchors.get(page)?.has(anchor), `${file}: ${target} names no heading of ${page}`);
      checked++;
    }
  }
  assert.ok(checked >= 40, `${checked} links to headings`);
});

test("an address of a heading is letters, digits, dashes and underscores", () => {
  assert.equal(validAnchor("code-editor"), "code-editor");
  assert.equal(validAnchor("#code-editor"), "code-editor");
  assert.equal(validAnchor("r%C3%A9sum%C3%A9_2"), "résumé_2");
  for (const anchor of [null, undefined, "", "#", "a b", "a\"]", "a]x[data-x", "x".repeat(201), "L10:5", "<script>"]) assert.equal(validAnchor(anchor), null, String(anchor));
});

test("a link names a page of the guide from the folder of the guide, and nothing else", () => {
  assert.deepEqual(resolveDocumentationLink("sessions.md", pages), { page: "sessions.md", anchor: null });
  assert.deepEqual(resolveDocumentationLink("plugins/git.md#sign-in", pages), { page: "plugins/git.md", anchor: "sign-in" });
  assert.deepEqual(resolveDocumentationLink("Plugins/GIT.md#Sign-In", pages), { page: "plugins/git.md", anchor: "Sign-In" });
  // A page the navigation does not name is still a page.
  assert.deepEqual(resolveDocumentationLink("orphan.md#", pages), { page: "orphan.md", anchor: null });
  assert.deepEqual(resolveDocumentationLink("sessions.md#a b", pages), { page: "sessions.md", anchor: null });
  for (const address of ["", "#queue", "nowhere.md", "../outside.md", "plugins/../sessions.md", "./sessions.md", "/sessions.md", "plugins//git.md", "plugins\\git.md", "%2e%2e/sessions.md",
    "C:/guide/sessions.md", "file:///C:/guide/sessions.md", "file:sessions.md", "https://example.com/sessions.md", "//server/sessions.md", "sessions.md:12", "src/Program.cs", "a".repeat(300) + ".md"]) {
    assert.equal(resolveDocumentationLink(address, pages), null, address);
  }
});

test("the navigation is entries with the entries of their folder, in the order of the menu", () => {
  const nodes = menuNodes(menu);
  assert.deepEqual(nodes.map(node => [node.item.path, node.children.map(child => child.path)]),
    [["readme.md", []], ["sessions.md", []], ["plugins/readme.md", ["plugins/git.md", "plugins/mcp.md"]], ["troubleshooting.md", []]]);
  // An entry whose folder is not listed is an entry of its own.
  assert.deepEqual(menuNodes([item("a.md", "A"), item("x/b.md", "B", 1, "x/readme.md")]).map(node => node.item.path), ["a.md", "x/b.md"]);
  assert.deepEqual(menuNodes([]), []);

  assert.equal(menuOwner(menu, "plugins/git.md"), "plugins/readme.md");
  assert.equal(menuOwner(menu, "plugins/readme.md"), "plugins/readme.md");
  assert.equal(menuOwner(menu, "sessions.md"), "sessions.md");
  assert.equal(menuOwner(menu, "orphan.md"), null);
  assert.equal(menuOwner(menu, null), null);

  assert.deepEqual(menuTrail(menu, "plugins/mcp.md").map(entry => entry.title), ["Plugins", "MCP"]);
  assert.deepEqual(menuTrail(menu, "sessions.md").map(entry => entry.title), ["Sessions"]);
  assert.deepEqual(menuTrail(menu, "orphan.md"), []);

  assert.deepEqual(menuNeighbors(menu, "readme.md"), { previous: null, next: menu[1] });
  assert.deepEqual(menuNeighbors(menu, "plugins/readme.md"), { previous: menu[1], next: menu[3] });
  assert.deepEqual(menuNeighbors(menu, "plugins/mcp.md"), { previous: menu[3], next: menu[5] });
  assert.deepEqual(menuNeighbors(menu, "troubleshooting.md"), { previous: menu[4], next: null });
  assert.deepEqual(menuNeighbors(menu, "orphan.md"), { previous: null, next: null });
});

test("an entry has the icon the window has for the name of the menu, or the icon of a page", () => {
  assert.equal(menuIcon("book"), "documentation");
  assert.equal(menuIcon("puzzle"), "plugin");
  assert.equal(menuIcon("lightning-charge"), "automation");
  for (const name of [null, undefined, "", "not-an-icon", "constructor", "toString", "__proto__"]) assert.equal(menuIcon(name), "fileText", String(name));
});

test("the tab remembers where it went, and going back drops nothing until another place is visited", () => {
  const a = { page: "readme.md", anchor: null }, b = { page: "sessions.md", anchor: "queue" }, c = { page: "plugins/git.md", anchor: null };
  let history = pushHistory(pushHistory(pushHistory(emptyHistory, a), b), c);
  assert.deepEqual(history, { entries: [a, b, c], index: 2 });
  // Going where the tab already is adds nothing.
  assert.equal(pushHistory(history, { page: "plugins/git.md", anchor: null }), history);
  assert.equal(moveHistory(history, 1), history);
  history = moveHistory(moveHistory(history, -1), -1);
  assert.equal(history.index, 0);
  assert.equal(moveHistory(history, -1), history);
  assert.equal(moveHistory(history, 1).index, 1);
  // Another place from the middle drops what was ahead.
  const elsewhere = { page: "troubleshooting.md", anchor: null };
  assert.deepEqual(pushHistory(moveHistory(history, 1), elsewhere), { entries: [a, b, elsewhere], index: 2 });
  assert.equal(moveHistory(emptyHistory, -1), emptyHistory);
  let long = emptyHistory;
  for (let index = 0; index < 150; index++) long = pushHistory(long, { page: `p${index}.md`, anchor: null });
  assert.equal(long.entries.length, 100);
  assert.equal(long.index, 99);
  assert.equal(long.entries[99].page, "p149.md");
});

test("the text that was searched for is marked in a line, whatever its case", () => {
  assert.deepEqual(highlightParts("A session keeps a Prompt Queue.", "prompt queue"), [{ text: "A session keeps a ", match: false }, { text: "Prompt Queue", match: true }, { text: ".", match: false }]);
  assert.deepEqual(highlightParts("queue, Queue", " QUEUE "), [{ text: "queue", match: true }, { text: ", ", match: false }, { text: "Queue", match: true }]);
  assert.deepEqual(highlightParts("nothing here", "queue"), [{ text: "nothing here", match: false }]);
  assert.deepEqual(highlightParts("anything", "  "), [{ text: "anything", match: false }]);
  // A letter whose lower case is longer: the line is shown without a mark rather than marked at the wrong place.
  assert.deepEqual(highlightParts("İstanbul queue", "queue"), [{ text: "İstanbul queue", match: false }]);
});

test("the Documentation tab is one tab of no project, kept and restored like the other pages of the window", () => {
  assert.equal(isDocumentationTab(documentationTab), true);
  let tabs = openFileTab(emptyFileTabs(), documentationTab);
  assert.equal(tabs.open.length, 1);
  assert.equal(openFileTab(tabs, { ...documentationTab }), tabs, "It opens once.");
  // It lasts whatever the projects of the space.
  tabs = reconcileFileTabs(tabs, { configured: true, projects: [], sessions: [], projectsTruncated: false, sessionsTruncated: false, displayTextTruncated: false });
  assert.equal(tabs.open.length, 1);
  let stored = "";
  persistFileTabs(value => { stored = value; }, tabs);
  const restored = restoreFileTabs(() => stored)!;
  assert.deepEqual(restored.open.map(fileTabKey), [fileTabKey(documentationTab)]);
  assert.equal(isDocumentationTab(restored.open[0]), true);
  // A stored tab that names a project is not this tab.
  const foreign = restoreFileTabs(() => JSON.stringify({ version: 1, open: [documentationTab, { projectId: "p", projectPath: "/p", view: "documentation" }], active: null }))!;
  assert.deepEqual(foreign.open.map(tab => tab.projectId), [""]);
});

test("/documentation is a command of the window, and its words are translated", () => {
  const command = commandDefinitions.find(candidate => candidate.id === "documentation");
  assert.ok(command);
  assert.equal(command.name, "documentation");
  assert.equal(commandDefinitions.filter(candidate => candidate.name === "documentation").length, 1);
  assert.ok(command.label in messages && command.description in messages);
  for (const [key, row] of Object.entries(documentationMessages)) {
    assert.equal(row.length, 5, key);
    assert.equal(messages[key as keyof typeof messages], row, `${key} is defined once`);
  }
});
