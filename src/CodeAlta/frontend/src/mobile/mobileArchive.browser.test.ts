import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import { session } from "./fixtureServer";
import { openPhone, tapMenuItem } from "./phoneBrowser";
import { testLimit } from "../testTime";

// Drags the card of a session to the right past the threshold and releases it; gives the card's transform after the release.
const swipeAway = (id: string) => `(() => {
  const card = document.querySelector('.row[data-id="${id}"] .card'), box = card.getBoundingClientRect(), y = box.top + box.height / 2;
  const fire = (type, x) => card.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerId: 7, button: 0, clientX: x, clientY: y }));
  fire("pointerdown", box.left + 10); fire("pointermove", box.left + 30); fire("pointermove", box.left + 10 + box.width * 0.6); fire("pointerup", box.left + 10 + box.width * 0.6);
  return card.style.transform;
})()`;

test("archiving asks about descendants even outside the loaded page and restores only what changed", { skip: !browserExecutable, timeout: testLimit(120_000) }, async () => {
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
    // The toast comes before the row leaves: the row that comes back is the restored one.
    await waitFor(`!document.querySelector('.row[data-id="parent"]')`, "parent out of the list");
    await click("#toast button");
    await waitFor(`document.querySelector('.row[data-id="parent"]')`, "restored parent");
    await click('.row[data-id="parent"] .card-archive');
    await waitFor('!document.getElementById("archive-sheet").hidden', "archive question third time");
    await click("#archive-yes");
    // The request itself: the toast of the archive before, hidden by the restore, still says "Archived".
    const archives = () => server.requests.filter(r => r.path === "/api/sessions/parent/archive");
    for (const deadline = Date.now() + testLimit(8000); archives().length < 2 && Date.now() < deadline;) await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(archives().length, 2, "the family is archived");
    assert.equal(archives().at(-1)?.query, "?withChildren=true");
    await waitFor('!document.getElementById("toast").hidden && document.querySelector("#toast .toast-text").textContent.startsWith("Archived")', "archived family");
    await waitFor(`!document.querySelector('.row[data-id="parent"]') && !document.querySelector('.row[data-id="child"]')`, "family out of the list");
    await click("#toast button");
    await waitFor(`document.querySelector('.row[data-id="parent"]')`, "restored family");
    await evaluate(`(async () => { for (let i = 0; i < 80; i++) { if ((await fetch('/api/sessions/child')).ok) return; await new Promise(r => setTimeout(r, 50)); } throw new Error('child not restored'); })()`);
    assert.ok(server.requests.some(r => r.path === "/api/sessions/child/restore"));
    assert.ok(server.state.sessions.some(s => s.id === "child"));
    assert.ok(!server.state.sessions.some(s => s.id === "old"), "a child archived before this action stays archived");
    assert.ok(!server.requests.some(r => r.path === "/api/sessions/old/restore"));
    // A swipe: the card leaves at once, and comes back while the phone asks about the child sessions.
    assert.match(String(await evaluate(swipeAway("parent"))), /translateX/);
    await waitFor('!document.getElementById("archive-sheet").hidden', "question after a swipe");
    assert.equal(await evaluate(`document.querySelector('.row[data-id="parent"] .card').style.transform`), "");
    await click("#archive-cancel");
    assert.match(String(await evaluate(swipeAway("other0"))), /translateX/);
    await waitFor('document.querySelector("#toast .toast-text").textContent.startsWith("Archived “Other")', "swiped session archived");
    assert.equal(server.requests.filter(r => r.path === "/api/sessions/other0/archive").at(-1)?.query, "");
    assert.deepEqual(phone.failures, []);
  } finally { await phone.close(); }
});
