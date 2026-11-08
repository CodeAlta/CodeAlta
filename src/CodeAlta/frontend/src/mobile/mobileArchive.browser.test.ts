import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { session } from "./fixtureServer";
import { openPhone, tapMenuItem } from "./phoneBrowser";

test("archiving asks about descendants even outside the loaded page and restores only what changed", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const phone = await openPhone(server => {
    server.state.sessions = [session("parent", "Parent", "p1", 1), ...Array.from({ length: 7 }, (_, i) => session(`other${i}`, "Other", "p1", 2 + i)),
      { ...session("child", "Child", "p1", 30), parentId: "parent" },
      { ...session("old", "Previously archived", "p1", 31), parentId: "parent" }];
  }, "");
  const { server, click, waitFor, evaluate } = phone;
  try {
    await phone.signIn();
    await evaluate(`fetch('/api/sessions/old/archive', { method: 'POST' })`);
    await click('.row[data-id="parent"] .card-archive');
    await waitFor('!document.getElementById("archive-sheet").hidden', "archive question");
    assert.equal(await evaluate('document.activeElement.id'), "archive-yes");
    assert.equal(server.requests.filter(r => r.path === "/api/sessions/parent/archive").length, 0);
    await click("#archive-cancel");
    assert.equal(server.requests.filter(r => r.path === "/api/sessions/parent/archive").length, 0);
    await click('.row[data-id="parent"] .card-link');
    await waitFor(`location.hash === '#/session/parent'`, "parent page");
    await evaluate(tapMenuItem("#menu-archive"));
    await waitFor('!document.getElementById("archive-sheet").hidden', "question on the session page");
    await click("#archive-cancel");
    assert.equal(await evaluate("location.hash"), "#/session/parent");
    await click("#back");
    await waitFor(`location.hash === '#/'`, "back to the list");
    await click('.row[data-id="parent"] .card-archive');
    await waitFor('!document.getElementById("archive-sheet").hidden', "archive question again");
    await click("#archive-no");
    await waitFor('document.querySelector("#toast .toast-text").textContent.startsWith("Archived")', "archived parent");
    assert.equal(server.requests.find(r => r.path === "/api/sessions/parent/archive")?.query, "");
    await click("#toast button");
    await waitFor(`document.querySelector('.row[data-id="parent"]')`, "restored parent");
    await click('.row[data-id="parent"] .card-archive');
    await waitFor('!document.getElementById("archive-sheet").hidden', "archive question third time");
    await click("#archive-yes");
    await waitFor('document.querySelector("#toast .toast-text").textContent.startsWith("Archived")', "archived family");
    assert.equal(server.requests.filter(r => r.path === "/api/sessions/parent/archive").at(-1)?.query, "?withChildren=true");
    await click("#toast button");
    await waitFor(`document.querySelector('.row[data-id="parent"]')`, "restored family");
    await evaluate(`(async () => { for (let i = 0; i < 80; i++) { if ((await fetch('/api/sessions/child')).ok) return; await new Promise(r => setTimeout(r, 50)); } throw new Error('child not restored'); })()`);
    assert.ok(server.requests.some(r => r.path === "/api/sessions/child/restore"));
    assert.ok(server.state.sessions.some(s => s.id === "child"));
    assert.ok(!server.state.sessions.some(s => s.id === "old"), "a child archived before this action stays archived");
    assert.ok(!server.requests.some(r => r.path === "/api/sessions/old/restore"));
    assert.deepEqual(phone.failures, []);
  } finally { await phone.close(); }
});
