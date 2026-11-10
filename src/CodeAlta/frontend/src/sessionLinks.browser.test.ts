import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";
import { testLimit } from "./testTime";

const options = { entry: "../sessionLinks.mount.tsx", fixture: "sessionLinkFixture", rpcFixture: "../demo-api.ts", width: 900, height: 650 } as const;
const chips = `Array.from(document.querySelectorAll('#live .markdown-content a[data-session-link]'))`;

test("a link to a session is a chip that says what the session is doing and opens it", { skip: !edge, timeout: testLimit(90_000) }, async () => {
  await withCanvas(async page => {
    await page.until(`${chips}.length === 4`, "the chips of the four sessions");
    assert.deepEqual(await page.evaluate(`${chips}.map(link => [link.textContent, link.getAttribute('data-session-link'), link.getAttribute('data-session-state'),
      link.getAttribute('data-session-scope')])`), [
      ["Parser review", "s-run", "running", "CodeAlta"], ["Notes", "s-idle", "idle", "Chats"], ["Old", "s-old", "archived", null], ["Gone", "s-gone", "unknown", null]]);
    // The tooltip says the state and the project; an unknown session says so.
    const titles = await page.evaluate<string[]>(`${chips}.map(link => link.title)`);
    assert.match(titles[0], /^Session Parser review: Running, in CodeAlta, last active /);
    assert.equal(titles[3], "Unknown session s-gone");
    // The project follows the title, and an unknown session is muted.
    assert.equal(await page.evaluate(`getComputedStyle(${chips}[0], '::after').content`), '"CodeAlta"');
    assert.equal(await page.evaluate(`getComputedStyle(${chips}[3]).borderTopStyle`), "dashed");
    // A link of another scheme is no link, and the links of an inert text are no chips.
    assert.equal(await page.evaluate(`document.querySelector('#live .markdown-content a:not([href])')?.textContent ?? document.querySelector('#live .markdown-content a[href^="sessions:"]')`), "raw");
    assert.equal(await page.evaluate(`document.querySelectorAll('#inert a[data-session-link]').length`), 0);

    // A click opens the session, at the message the link names; an unknown session is asked for too, as it may have just started.
    await page.click(`#live a[data-session-link="s-run"]`);
    await page.click(`#live a[data-session-link="s-gone"]`);
    await page.until(`sessionLinkFixture.opened.length === 2`, "the sessions opened");
    assert.deepEqual(await page.evaluate(`sessionLinkFixture.opened`), [{ sessionId: "s-run", offset: "120" }, { sessionId: "s-gone", offset: null }]);
    assert.deepEqual(await page.evaluate(`sessionLinkFixture.links`), [], "a link to a session is not given to the opener of web and file links");

    // Its menu opens it, and copies its id or the link.
    const box = await page.evaluate<{ x: number; y: number; width: number; height: number }>(`document.querySelector('#live a[data-session-link="s-idle"]').getBoundingClientRect().toJSON()`);
    await page.command("Input.dispatchMouseEvent", { type: "mousePressed", x: box.x + 4, y: box.y + box.height / 2, button: "right", clickCount: 1 });
    await page.command("Input.dispatchMouseEvent", { type: "mouseReleased", x: box.x + 4, y: box.y + box.height / 2, button: "right", clickCount: 1 });
    await page.until(`document.querySelectorAll('[role=menuitem]').length === 3`, "the menu of the link");
    assert.deepEqual(await page.evaluate(`Array.from(document.querySelectorAll('[role=menuitem]'), item => item.textContent.trim())`), ["Open", "Copy session id", "Copy link"]);
    await page.evaluate(`Array.from(document.querySelectorAll('[role=menuitem]')).find(item => item.textContent.trim() === 'Copy session id').click()`);
    await page.until(`sessionLinkFixture.copies.length === 1`, "the id copied");
    assert.deepEqual(await page.evaluate(`sessionLinkFixture.copies`), ["s-idle"]);

    // The chip follows the state of its session.
    await page.evaluate(`sessionLinkFixture.stop()`);
    await page.until(`${chips}[0].getAttribute('data-session-state') === 'idle'`, "the session that stopped running");
    assert.deepEqual(page.problems, []);
  }, options);
});
