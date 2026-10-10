import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";

// The menu of a session row of the Explorer whose scope is open and not selected (a chat, a session of another project), in
// headless Edge under the production content security policy and React StrictMode: the lines that plugins add are those of the
// row that was clicked, and using one neither opens the session nor changes what is selected.

const options = { entry: "../explorer/explorerSessionMenu.mount.tsx", fixture: "explorerFixture" } as const;
const statistics = `explorerFixture.wire({ buttonId: "statistics-session", label: "Statistics of this session", commandId: "builtin:statistics/command/statistics-session" })`;
const menuLines = (page: Page) => page.evaluate<string[]>(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].map(item => item.textContent.trim())`);
const openMenu = async (page: Page, scope: string, session: string) => {
  await page.click(`[data-scope="${scope}"] .session-actions-trigger[aria-label*="(ID: ${session})"]`);
  await page.until(`document.querySelector('.session-tab-popup [role="menuitem"]')`, `the menu of ${session}`);
};
const sessionMenuReads = (page: Page) => page.evaluate<{ projectId: string | null; sessionId: string | null }[]>(
  `explorerFixture.state.reads.filter(read => read.place === 'SessionMenu').map(read => ({ projectId: read.projectId, sessionId: read.sessionId }))`);

test("the menu of a session row outside the selected scope has the lines of plugins, for the row that was clicked", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`explorerFixture.setButtons([${statistics}]); explorerFixture.render()`);
    await page.until(`document.querySelectorAll('.session-row').length === 4`, "the rows of the chats and of the other project");
    // The title bar asks about the selected project and session; no menu is open, so no row is asked about.
    await page.until(`explorerFixture.state.reads.some(read => read.place === 'TitleBar')`, "the buttons of the title bar");
    assert.deepEqual(await sessionMenuReads(page), []);

    // A chat, while a project and one of its sessions are selected.
    await openMenu(page, "chats", "chat-2");
    await page.until(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].some(item => item.textContent.trim() === 'Statistics of this session')`, "the line of the plugin in the menu of the chat");
    assert.deepEqual(await menuLines(page), ["Open session", "Rename…", "Delete…", "Statistics of this session"]);
    assert.ok((await sessionMenuReads(page)).length > 0);
    assert.ok((await sessionMenuReads(page)).every(read => read.projectId === null && read.sessionId === "chat-2"), "the host is asked about the chat of the row, never about the selected session");
    await page.clickText('.session-tab-popup [role="menuitem"]', "Statistics of this session");
    await page.until(`explorerFixture.state.activated.length === 1`, "the line of the plugin");
    assert.deepEqual(await page.evaluate(`explorerFixture.state.activated[0]`),
      { button: "statistics-session", commandId: "builtin:statistics/command/statistics-session", projectId: null, sessionId: "chat-2" });
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed");

    // A session of another project, a sub-agent: its project and its own id.
    await page.evaluate(`explorerFixture.state.reads.length = 0`);
    await openMenu(page, "project-b", "b-2");
    await page.until(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].some(item => item.textContent.trim() === 'Statistics of this session')`, "the line of the plugin in the menu of the other project");
    assert.ok((await sessionMenuReads(page)).every(read => read.projectId === "project-b" && read.sessionId === "b-2"));
    // With the keyboard: the last line of the menu.
    await page.key("End", 35);
    await page.key("Enter", 13, "\r");
    await page.until(`explorerFixture.state.activated.length === 2`, "the line of the plugin by the keyboard");
    assert.deepEqual(await page.evaluate(`explorerFixture.state.activated[1]`),
      { button: "statistics-session", commandId: "builtin:statistics/command/statistics-session", projectId: "project-b", sessionId: "b-2" });

    // Nothing was opened, renamed or deleted: the selection is as it was, and so is what the title bar is asked about.
    assert.deepEqual(await page.evaluate(`explorerFixture.state.actions`), []);
    assert.ok(await page.evaluate(`explorerFixture.state.reads.filter(read => read.place === 'TitleBar').every(read => read.projectId === 'project-a' && read.sessionId === 'a-1')`));

    // The lines of the application still do what they did, for the same row.
    await openMenu(page, "chats", "chat-1");
    await page.clickText('.session-tab-popup [role="menuitem"]', "Open session");
    await page.until(`explorerFixture.state.actions.length === 1`, "the session opened");
    assert.deepEqual(await page.evaluate(`explorerFixture.state.actions`), [{ session: "chat-1", action: "open" }]);
    assert.equal(await page.evaluate(`explorerFixture.state.activated.length`), 2);
  }, options);
});

test("the menu of a session row outside the selected scope opens the session canvases for that row and its own project", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    const lineOf = (label: string) => `[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].some(item => item.textContent.trim() === ${JSON.stringify(label)})`;
    // Two canvases about a session, beside canvases of other scopes that a session menu does not list.
    await page.evaluate(`explorerFixture.setCanvases([explorerFixture.canvas("status", "Application"), explorerFixture.canvas("board", "Session"), explorerFixture.canvas("steps", "Project"), explorerFixture.canvas("notes", "Session")]);
      explorerFixture.setButtons([${statistics}]); explorerFixture.render()`);
    await page.until(`document.querySelectorAll('.session-row').length === 4`, "the rows of the chats and of the other project");

    // A chat, while project-a is selected: the canvas is about the chat and no project.
    await openMenu(page, "chats", "chat-2");
    await page.until(lineOf("Statistics of this session"), "the lines of the menu of the chat");
    assert.deepEqual(await menuLines(page), ["Open session", "Rename…", "Delete…", "Open Board", "Open Notes", "Statistics of this session"], "the canvases, then the lines of plugins, as in the selected scope");
    assert.equal(await page.evaluate(`document.querySelectorAll('.session-tab-popup .bp6-menu-divider').length`), 2);
    await page.clickText('.session-tab-popup [role="menuitem"]', "Open Board");
    await page.until(`explorerFixture.state.canvases.length === 1`, "the canvas of the chat");
    assert.deepEqual(await page.evaluate(`explorerFixture.state.canvases[0]`), { canvas: "board", project: null, sessionId: "chat-2" });
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed by its line");
    assert.equal(await page.evaluate(`document.querySelector('[data-scope="chats"] .session-row.menu-open')`), null);

    // A session of another project: its own project, with its path; neither the selected project nor none.
    await openMenu(page, "project-b", "b-2");
    await page.until(lineOf("Open Notes"), "the lines of the menu of the other project");
    await page.clickText('.session-tab-popup [role="menuitem"]', "Open Notes");
    await page.until(`explorerFixture.state.canvases.length === 2`, "the canvas of the session of the other project");
    assert.deepEqual(await page.evaluate(`explorerFixture.state.canvases[1]`), { canvas: "notes", project: { id: "project-b", path: "/code/b" }, sessionId: "b-2" });
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed again");

    // Escape closes the menu and opens nothing; the row keeps the keyboard.
    await openMenu(page, "project-b", "b-1");
    await page.until(lineOf("Open Board"), "the menu of another row");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed by Escape");
    assert.equal(await page.evaluate(`explorerFixture.state.canvases.length`), 2);

    // Nothing was opened as a session, nothing was run, and what is selected is as it was.
    assert.deepEqual(await page.evaluate(`explorerFixture.state.actions`), []);
    assert.equal(await page.evaluate(`explorerFixture.state.activated.length`), 0);
    assert.ok(await page.evaluate(`explorerFixture.state.reads.filter(read => read.place === 'TitleBar').every(read => read.projectId === 'project-a' && read.sessionId === 'a-1')`));

    // More canvases than a menu lists: the page of the canvases is one line away, with the keyboard too.
    await page.evaluate(`explorerFixture.setCanvases(["a", "b", "c", "d", "e"].map(id => explorerFixture.canvas(id, "Session"))); explorerFixture.setButtons([]); explorerFixture.clear(); explorerFixture.render()`);
    await page.until(`document.querySelectorAll('.session-row').length === 4`, "the rows again");
    await openMenu(page, "chats", "chat-1");
    await page.until(lineOf("More…"), "the line of the page of the canvases");
    assert.deepEqual(await menuLines(page), ["Open session", "Rename…", "Delete…", "Open A", "Open B", "Open C", "Open D", "More…"]);
    await page.key("End", 35);
    await page.key("Enter", 13, "\r");
    await page.until(`explorerFixture.state.pages === 1`, "the page of the canvases");
    assert.equal(await page.evaluate(`explorerFixture.state.canvases.length`), 2);
  }, options);
});

test("a menu with no line of a plugin is as it was, and a line a plugin cannot use is shown as such", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`explorerFixture.setButtons([]); explorerFixture.render()`);
    await page.until(`document.querySelectorAll('.session-row').length === 4`, "the rows");
    await openMenu(page, "chats", "chat-1");
    await page.until(`explorerFixture.state.reads.some(read => read.place === 'SessionMenu' && read.sessionId === 'chat-1')`, "the host asked about the row");
    assert.deepEqual(await menuLines(page), ["Open session", "Rename…", "Delete…"]);
    assert.equal(await page.evaluate(`document.querySelectorAll('.session-tab-popup .bp6-menu-divider').length`), 0, "no separator without a line under it");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.session-tab-popup')`, "the menu closed");

    await page.evaluate(`explorerFixture.setButtons([explorerFixture.wire({ buttonId: "busy-only", label: "Needs a running session", disabled: true })]); explorerFixture.clear(); explorerFixture.render()`);
    await page.until(`document.querySelectorAll('.session-row').length === 4`, "the rows again");
    await openMenu(page, "project-b", "b-1");
    await page.until(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].some(item => item.textContent.trim() === 'Needs a running session')`, "the line that cannot be used");
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.session-tab-popup [role="menuitem"]')].find(item => item.textContent.trim() === 'Needs a running session').getAttribute('aria-disabled')`), "true");
    assert.equal(await page.evaluate(`explorerFixture.state.activated.length`), 0);
  }, options);
});
