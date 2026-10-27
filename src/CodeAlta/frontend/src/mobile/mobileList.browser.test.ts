import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import type { ActivityFacts, Live } from "./contract";
import { session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const minute = 60_000;
const ago = (minutes: number) => new Date(Date.now() - minutes * minute).toISOString();
const reading: ActivityFacts = { state: "running", headline: "Reading the grammar", thoughts: 2, tools: 5, succeeded: 4, failed: 0, running: 1, startedAt: ago(1.5) };

test("the phone lists sub-agents under their parent, folded, and keeps its order still while it is looked at", { skip: !browserExecutable, timeout: 120_000 }, async () => {
  const phone = await openPhone(server => {
    server.state.sessions = [
      session("root", "Refactor the parser into modules", "p1", 20, {}, { worktree: "fond-peak" }),
      session("c1", "Map the tokenizer", "p1", 1, { live: "running", kind: "activity", line: null, activity: reading }, { parentId: "root" }),
      session("c2", "Review the grammar", "p1", 3, { line: "The grammar has two ambiguous rules." }, { parentId: "root" }),
      session("c3", "Split the modules", "p1", 4, { line: "Three modules, one per stage." }, { parentId: "root", agent: { id: "plan", name: "Plan" } }),
      session("g1", "Name the modules", "p1", 5, { live: "thinking", kind: "assistant", line: "Weighing the names" }, { parentId: "c3" }),
      session("s3", "Write the docs of the site", "p2", 6, { live: "running", kind: "writing", line: "Writing the install page…" }),
      session("s4", "Fix the flaky test", "p1", 90),
      session("o1", "Check the release notes", "p2", 120, {}, { parentId: "elsewhere", parentTitle: "Prepare the release" }),
    ];
  }, shots);
  const { server, evaluate, waitFor, click, texts, rows, shot } = phone;
  const push = (id: string, live: Partial<Live>) => server.push({ ...server.state.sessions.find(s => s.id === id)!.live!, at: new Date().toISOString(), ...live });
  const box = (selector: string) => evaluate<{ top: number; bottom: number; left: number; right: number }>(
    `(() => { const r = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect(); return { top: r.top, bottom: r.bottom, left: r.left, right: r.right }; })()`);
  const fits = () => evaluate<boolean>("document.documentElement.scrollWidth <= innerWidth && [...document.querySelectorAll('#sessions .card')].every(c => c.getBoundingClientRect().right <= innerWidth)");
  const pause = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  try {
    await phone.signIn();

    // A parent is as recent as its latest sub-agent, and its sub-agents are folded under one row of its card.
    assert.deepEqual(await rows(), ["root", "s3", "s4", "o1"]);
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"root\"] .subagents').getAttribute('aria-expanded')"), "false");
    assert.deepEqual(await texts('.row[data-id="root"] .subagents :is(.subagents-count, .subagents-states > span)'), ["3 sub-agents", "2 running"]);
    assert.equal(await evaluate("document.querySelectorAll('.row[data-id=\"root\"] .subagents-dots .dot').length"), 3, "a dot for each sub-agent it started");
    assert.ok(await evaluate<number>("document.querySelector('.row[data-id=\"root\"] .subagents').getBoundingClientRect().height") >= 44, "a touch target of 44 px");
    // A sub-agent whose parent is not listed is a card of its own that names it.
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"o1\"] .parent-line').textContent"), "Sub-agent of Prepare the release");
    assert.ok(await fits(), "no horizontal overflow");
    await shot("01-folded-dark.png");

    // A tap shows them under it, indented a little, joined by a line from the bottom of the card above; the sub-agent that started
    // others has its own fold.
    await click('.row[data-id="root"] .subagents');
    await waitFor(`document.querySelectorAll("#sessions .row").length === 7`, "the sub-agents shown");
    assert.deepEqual(await rows(), ["root", "c1", "c2", "c3", "s3", "s4", "o1"]);
    await pause(400); // the rows that came in have slid to their places
    assert.equal(await evaluate("JSON.parse(localStorage.getItem('alta.subagents')).root[0]"), true, "the choice is kept on the phone");
    const parent = await box('.row[data-id="root"] .card'), child = await box('.row[data-id="c1"] .card'), elbow = await box('.row[data-id="c1"] .elbow');
    assert.ok(child.left > parent.left && child.left - parent.left <= 16, `a sub-agent is indented a little (${child.left - parent.left} px)`);
    assert.equal(child.right, parent.right, "and as wide as it can be");
    assert.ok(Math.abs(elbow.top - parent.bottom) <= 1, `its line touches the card above (${elbow.top} vs ${parent.bottom})`);
    assert.ok(elbow.left > parent.left && Math.abs(elbow.right - child.left) <= 1, "it comes down from the parent and turns into the card");
    assert.deepEqual(await evaluate("[...document.querySelectorAll('#sessions .row.sub')].map(r => r.getAttribute('aria-level'))"), ["2", "2", "2"]);
    assert.deepEqual(await texts('.row[data-id="c3"] .meta-item'), ["GPT-6.1 Sol · low", "Plan"], "a sub-agent does not repeat the project of its parent");
    await click('.row[data-id="c3"] .subagents');
    await waitFor(`document.querySelector('.row[data-id="g1"]')`, "the sub-agent of a sub-agent");
    await pause(400);
    assert.deepEqual(await rows(), ["root", "c1", "c2", "c3", "g1", "s3", "s4", "o1"]);
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"g1\"]').style.getPropertyValue('--depth')"), "2");
    assert.ok(await fits(), "no horizontal overflow");
    await shot("02-open-dark.png");
    await click("#theme");
    await shot("03-open-light.png");
    await evaluate("document.documentElement.dataset.fontStep = '5'");
    assert.ok(await fits(), "no horizontal overflow at the largest text");
    await shot("04-open-light-large-text.png");
    await evaluate("document.documentElement.dataset.fontStep = '2'");

    // Folded again, a sub-agent that comes to wait for the user opens the fold by itself.
    await click('.row[data-id="root"] .subagents');
    await waitFor(`!document.querySelector('.row[data-id="c1"]')`, "folded again");
    push("c2", { asking: { id: "ask1", revision: "1", state: "pending", immediate: false } });
    await waitFor(`document.querySelector('.row[data-id="c2"] .waiting-badge')`, "the waiting sub-agent shown");
    assert.equal(await evaluate("document.querySelector('.row[data-id=\"c2\"] .card').classList.contains('waiting')"), true);
    assert.match(await evaluate<string>("document.querySelector('.row[data-id=\"root\"] .subagents-states').textContent"), /1 waits for you/);
    await shot("05-waiting-light.png");
    await click("#theme");
    await shot("06-waiting-dark.png");

    // A session that becomes the latest changes its card at once, and takes its place only once the list has been quiet.
    const before = await rows();
    push("s4", { live: "running", kind: "writing", line: "Writing the fix…" });
    await waitFor(`document.querySelector('.row[data-id="s4"] .card').classList.contains("running")`, "the card changed in place");
    await pause(1000);
    assert.deepEqual(await rows(), before, "the order holds while the list is looked at");
    // A finger down holds it longer.
    await evaluate("document.querySelector('#sessions .card').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerId: 1, isPrimary: true }))");
    await pause(5500);
    assert.deepEqual(await rows(), before, "nothing moves under a finger");
    await evaluate("window.dispatchEvent(new PointerEvent('pointerup', { pointerId: 1, isPrimary: true }))");
    await waitFor(`document.querySelector("#sessions .row").dataset.id === "s4"`, "the new order once quiet", 8000);
    assert.deepEqual(await rows(), ["s4", "root", "c2", "c1", "c3", "g1", "s3", "o1"]);

    // Back from a session, the list is in its latest order at once.
    push("o1", { live: "running", kind: "writing", line: "Reading the notes" });
    await waitFor(`document.querySelector('.row[data-id="o1"] .card').classList.contains("running")`, "the card changed in place");
    assert.equal((await rows())[0], "s4");
    await click('.row[data-id="s3"] .card-link');
    await waitFor(`!document.getElementById("detail").hidden`, "the page of the session");
    await evaluate("history.back()");
    await waitFor(`!document.getElementById("home").hidden`, "the list again");
    assert.equal((await rows())[0], "o1", "no wait once back");
    assert.deepEqual(phone.failures, [], "no error, no policy violation");
  } finally {
    await phone.close();
  }
});
