import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The tab of the bookmarks in headless Edge, under the production content security policy and React StrictMode.

const options = { entry: "../bookmarks/bookmarksPanel.mount.tsx", fixture: "bookmarksFixture", rpcFixture: "../demo-api.ts", styles: ["../bookmarks/bookmarks.css"], width: 1200, height: 800 } as const;
const projects = `[...document.querySelectorAll('.bookmarks-project')].map(project => [project.querySelector('h2 > span').textContent, project.querySelector('h2 .bookmarks-count').textContent, [...project.querySelectorAll('.bookmarks-session')].map(session => session.dataset.sessionId)])`;
const titles = `[...document.querySelectorAll('.bookmark-row .bookmark-text > strong')].map(title => title.textContent)`;

test("the bookmarks are listed by project and session, go to what they mark, and are searched and filtered", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.bookmarks-project').length === 3`, "the projects that have bookmarks");
    // The project with the newest bookmark first; the chats are a group of their own.
    assert.deepEqual(await page.evaluate(projects), [["Alpha", "3", ["ship", "design"]], ["Chats", "1", ["chat"]], ["Beta", "1", ["gone"]]]);
    assert.equal(await page.evaluate(`document.querySelector('.bookmarks-heading .bookmarks-count').textContent`), "5");
    // The messages of a session follow its conversation, and are named by their beginning without the marks of Markdown.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[data-session-id="ship"] .bookmark-row')].map(row => [row.dataset.bookmarkId, row.dataset.role])`), [["prompt", "user"], ["release", "assistant"]]);
    assert.deepEqual(await page.evaluate(titles), ["Check the two entries of the changelog.", "Both entries are implemented and checked: the tag triggers the release, and the package is on…", "The naming idea", "What was said."]);
    assert.equal(await page.evaluate(`document.querySelector('[data-bookmark-id="idea"] .bookmark-detail').textContent`), "Call it a rail.", "a name the user gave leaves the message under it");
    assert.ok(await page.evaluate(`document.querySelector('[data-bookmark-id="release"] .bookmark-detail').textContent.includes('a few minutes later')`), "a long message is read under its name");

    // Nothing is wider than the tab, and a row does not grow with its text.
    assert.equal(await page.evaluate(`(() => { const list = document.querySelector('.bookmarks-list'); return list.scrollWidth <= list.clientWidth && [...document.querySelectorAll('.bookmark-row')].every(row => row.scrollWidth <= row.clientWidth + 1); })()`), true);

    // A message opens its session at the message; a bookmarked session opens the session; a session of messages opens too.
    await page.click('[data-bookmark-id="release"] .bookmark-open');
    await page.click('[data-session-id="design"] .bookmarks-session-open');
    await page.click('[data-session-id="ship"] .bookmarks-session-open');
    assert.deepEqual(await page.evaluate(`[bookmarksFixture.state.gone, bookmarksFixture.state.opened]`), [["release", "whole"], ["ship"]]);
    assert.equal(await page.evaluate(`document.querySelector('[data-session-id="design"] .bookmarks-session-tag').textContent`), "Session bookmarked");

    // A session that no longer exists says so; its bookmarks lead nowhere and can still be removed.
    assert.equal(await page.evaluate(`document.querySelector('[data-session-id="gone"]').dataset.missing`), "true");
    assert.equal(await page.evaluate(`document.querySelector('[data-session-id="gone"] .bp6-tag').textContent`), "Session deleted");
    assert.equal(await page.evaluate(`document.querySelector('[data-bookmark-id="lost"] .bookmark-open').disabled && document.querySelector('[data-session-id="gone"] .bookmarks-session-open').disabled`), true);

    // The search looks in the names, the messages, the sessions and the projects.
    await page.click('[aria-label="Search bookmarks"]');
    await page.type("tag release");
    await page.until(`document.querySelectorAll('.bookmark-row').length === 1`, "the bookmark the search finds");
    assert.deepEqual(await page.evaluate(projects), [["Alpha", "1", ["ship"]]]);
    await page.evaluate(`document.querySelector('[aria-label="Search bookmarks"]').select()`);
    await page.type("beta");
    await page.until(`document.querySelector('.bookmarks-project')?.dataset.projectId === 'beta'`, "the bookmarks of a project by its name");
    await page.evaluate(`document.querySelector('[aria-label="Search bookmarks"]').select()`);
    await page.type("nothing like this");
    await page.until(`document.querySelector('.bookmarks-list .bp6-non-ideal-state')?.textContent.includes('No bookmark matches.')`, "a search that finds nothing");
    await page.evaluate(`document.querySelector('[aria-label="Search bookmarks"]').select()`);
    await page.key("Backspace", 8);
    await page.until(`document.querySelectorAll('.bookmarks-project').length === 3`, "everything again");

    // The filter keeps the bookmarks of sessions, or those of messages.
    assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[aria-label="Kind of bookmark"] button')].map(button => button.textContent)`), ["All 5", "Messages 4", "Sessions 1"]);
    await page.clickText('[aria-label="Kind of bookmark"] button', "Sessions 1");
    await page.until(`document.querySelectorAll('.bookmarks-session').length === 1 && document.querySelectorAll('.bookmark-row').length === 0`, "the bookmarked sessions");
    await page.clickText('[aria-label="Kind of bookmark"] button', "Messages 4");
    await page.until(`document.querySelectorAll('.bookmark-row').length === 4 && !document.querySelector('[data-session-id="design"]')`, "the bookmarked messages");
  }, options);
});

test("a bookmark is named and removed from the tab, and the tab says when the host refuses", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.bookmark-row').length === 4`, "the bookmarks");

    // Naming: the field has the name the user gave, Enter keeps it, and the message goes under the name.
    await page.click('[data-bookmark-id="prompt"] [aria-label^="Rename the bookmark"]');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Name of the bookmark'`, "the field of the name");
    assert.equal(await page.evaluate(`document.activeElement.value`), "");
    assert.equal(await page.evaluate(`document.activeElement.placeholder`), "Check the two entries of the changelog.");
    await page.type("Changelog check");
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('[data-bookmark-id="prompt"] .bookmark-text > strong')?.textContent === 'Changelog check'`, "the new name");
    assert.equal(await page.evaluate(`document.querySelector('[data-bookmark-id="prompt"] .bookmark-detail').textContent`), "Check the two entries of the changelog.");

    // Escape leaves the name as it was; an empty name gives the bookmark back the one of its message.
    await page.click('[data-bookmark-id="prompt"] [aria-label^="Rename the bookmark"]');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Name of the bookmark'`, "the field again");
    assert.equal(await page.evaluate(`document.activeElement.value`), "Changelog check");
    await page.type(" and more");
    await page.key("Escape", 27);
    await page.until(`!document.querySelector('.bookmark-rename')`, "the field closed");
    assert.equal(await page.evaluate(`document.querySelector('[data-bookmark-id="prompt"] .bookmark-text > strong').textContent`), "Changelog check");
    await page.click('[data-bookmark-id="prompt"] [aria-label^="Rename the bookmark"]');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Name of the bookmark'`, "the field a third time");
    await page.evaluate(`document.activeElement.select()`);
    await page.key("Backspace", 8);
    await page.clickText('.bookmark-rename button', "Save");
    await page.until(`document.querySelector('[data-bookmark-id="prompt"] .bookmark-text > strong')?.textContent === 'Check the two entries of the changelog.'`, "the name of the message again");
    assert.deepEqual(await page.evaluate(`bookmarksFixture.state.renames`), [["prompt", "Changelog check"], ["prompt", null]]);

    // The bookmark of a session is named too: its name leads, and the title of the session follows.
    await page.click('[data-session-id="design"] [aria-label^="Rename the bookmark"]');
    await page.until(`document.activeElement?.getAttribute('aria-label') === 'Name of the bookmark'`, "the field of the session");
    await page.type("The review to reread");
    await page.key("Enter", 13, "\r");
    await page.until(`document.querySelector('[data-session-id="design"] .bookmarks-session-open strong')?.textContent === 'The review to reread'`, "the name of the bookmarked session");
    assert.equal(await page.evaluate(`document.querySelector('[data-session-id="design"] .bookmarks-session-title').textContent`), "Design review");

    // Removing: the row goes, and so does a session that has nothing left.
    await page.click('[data-bookmark-id="lost"] [aria-label^="Remove the bookmark"]');
    await page.until(`!document.querySelector('[data-session-id="gone"]')`, "the bookmark of the deleted session removed");
    assert.equal(await page.evaluate(`document.querySelector('.bookmarks-heading .bookmarks-count').textContent`), "4");

    // A host that refuses: the row stays and the tab says so.
    await page.evaluate(`bookmarksFixture.state.refuse = true`);
    await page.click('[data-bookmark-id="idea"] [aria-label^="Remove the bookmark"]');
    await page.until(`bookmarksFixture.state.notices.length === 1`, "the refusal");
    assert.deepEqual(await page.evaluate(`bookmarksFixture.state.notices`), ["The bookmark could not be changed."]);
    assert.equal(await page.evaluate(`!!document.querySelector('[data-bookmark-id="idea"]')`), true);

    // A bookmark an agent adds is listed without anything being asked for.
    await page.evaluate(`bookmarksFixture.state.rows.unshift({ ...bookmarksFixture.state.rows[0], id: "agent", sessionId: "agent-session", sessionTitle: "Agent work", excerpt: "Added by an agent.", createdBy: "agent-session", createdAt: "2026-10-12T10:00:00Z" }); bookmarksFixture.changed()`);
    await page.until(`document.querySelector('[data-bookmark-id="agent"]')`, "the bookmark of the agent");

    // Nothing bookmarked, then a host that keeps no bookmarks.
    await page.evaluate(`bookmarksFixture.restart('ok', [])`);
    await page.until(`document.querySelector('.bookmarks-list .bp6-non-ideal-state')?.textContent.includes('No bookmarks yet')`, "the empty tab");
    assert.equal(await page.evaluate(`!!document.querySelector('.bookmarks-tools')`), false, "nothing to search");
    await page.evaluate(`bookmarksFixture.restart('unavailable')`);
    await page.until(`document.querySelector('.bookmarks-list .bp6-non-ideal-state')?.textContent.includes('Bookmarks are not available here.')`, "no bookmarks here");
  }, options);
});

test("the tab of the bookmarks is drawn in both themes and in a narrow window", { skip: !edge, timeout: 120_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`document.querySelectorAll('.bookmark-row').length === 4`, "the bookmarks");
    const overflow = `(() => { const list = document.querySelector('.bookmarks-list'), header = document.querySelector('.bookmarks-header'); return list.scrollWidth > list.clientWidth || header.scrollWidth > header.clientWidth; })()`;
    await page.shot("bookmarks-dark");
    assert.equal(await page.evaluate(overflow), false);
    await page.evaluate(`document.documentElement.dataset.theme = 'light'`);
    await page.shot("bookmarks-light");
    assert.equal(await page.evaluate(overflow), false);
    await page.resize(520, 700);
    await page.until(`innerWidth === 520`, "the narrow window");
    await page.shot("bookmarks-narrow");
    assert.equal(await page.evaluate(overflow), false, "nothing is wider than a narrow tab");
    // The actions of a row stay beside it, inside the tab.
    assert.equal(await page.evaluate(`[...document.querySelectorAll('.bookmark-actions')].every(actions => { const box = actions.getBoundingClientRect(); return box.right <= innerWidth && box.width > 0; })`), true);
  }, options);
});
