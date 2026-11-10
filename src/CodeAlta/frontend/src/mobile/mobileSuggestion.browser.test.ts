import assert from "node:assert/strict";
import test from "node:test";
import { browserExecutable } from "../browserTarget";
import type { Live } from "./contract";
import { session } from "./fixtureServer";
import { openPhone } from "./phoneBrowser";
import { testLimit } from "../testTime";

// Where the pictures of this test go, when a person wants to look at them.
const shots = process.env.CODEALTA_SHOTS ?? "";
const long = "run the whole test suite of the parser again, fix what fails, then commit the change on a branch named for the fix";

test("the phone offers the suggested next prompt as a chip that fills the box, never sends, and goes while typing or working", { skip: !browserExecutable, timeout: testLimit(90_000) }, async () => {
  const phone = await openPhone(server => {
    server.state.sessions = [session("s1", "Fix the parser", "p1", 1, { line: "The parser is fixed.", suggestion: long })];
  }, shots);
  const { server, evaluate, waitFor, click } = phone;
  const push = (live: Partial<Live>) => server.push({ ...server.state.sessions[0]!.live!, at: new Date().toISOString(), ...live });
  const chipShown = '!document.getElementById("suggestion").hidden';
  const type = (text: string) => evaluate(`(() => { const box = document.getElementById("prompt"); box.value = ${JSON.stringify(text)}; box.dispatchEvent(new Event("input", { bubbles: true })); })()`);
  const prompts = () => server.requests.filter(request => request.method === "POST" && request.path.endsWith("/prompt"));
  try {
    await phone.signIn();
    await click('.row[data-id="s1"] .card-link');
    await waitFor(chipShown, "the chip of the suggestion");

    // One line over the box, cut to the width of the phone; the whole text is in its label.
    assert.equal(await evaluate('document.getElementById("suggestion").textContent'), `↳${long}`);
    assert.equal(await evaluate('document.getElementById("suggestion").getAttribute("aria-label")'), `Use suggestion: ${long}`);
    assert.equal(await evaluate('document.getElementById("suggestion").nextElementSibling.id'), "prompt", "just above the box");
    assert.equal(await evaluate('(() => { const text = document.getElementById("suggestion-text"); return text.scrollWidth > text.clientWidth && getComputedStyle(text).textOverflow === "ellipsis"; })()'), true, "a long one is cut with an ellipsis");
    assert.equal(await evaluate("document.documentElement.scrollWidth <= innerWidth"), true, "no horizontal overflow");
    assert.equal(await evaluate('document.getElementById("prompt").placeholder'), "Ask for more…", "the placeholder stays the composer's own");
    await phone.shot("01-suggestion.png");

    // A tap fills the box, to edit and send, and sends nothing.
    await click("#suggestion");
    assert.equal(await evaluate('document.getElementById("prompt").value'), long);
    assert.equal(await evaluate('document.activeElement.id'), "prompt");
    assert.equal(await evaluate('document.getElementById("prompt").selectionStart'), long.length, "the cursor at its end");
    assert.equal(await evaluate(chipShown), false, "the box holds text: no chip");
    assert.equal(prompts().length, 0, "nothing sent");

    // Typing hides it; an empty box brings it back.
    await type("");
    await waitFor(chipShown, "back once the box is empty");
    await type("something else");
    assert.equal(await evaluate(chipShown), false);
    await type("");
    await waitFor(chipShown, "back once the box is empty again");

    // A turn that runs hides it; the next turn brings its own.
    push({ live: "running", kind: "writing", line: "Writing…", suggestion: null });
    await waitFor(`!(${chipShown})`, "hidden while a turn runs");
    push({ live: "idle", kind: "assistant", line: "Done.", suggestion: "go ahead" });
    await waitFor(`${chipShown} && document.getElementById("suggestion-text").textContent === "go ahead"`, "the next suggestion");

    // Once a prompt is sent after it, it is not offered again before the session says that its next turn started.
    await click("#suggestion");
    await click("#send");
    await waitFor(`!document.getElementById("composer").classList.contains("sending")`, "the prompt sent");
    assert.equal(prompts().length, 1);
    assert.equal(await evaluate(chipShown), false, "the suggestion was answered");
    push({ live: "idle", kind: "assistant", line: "Done.", suggestion: "go ahead" });
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await evaluate(chipShown), false, "still answered");
    assert.deepEqual(phone.failures, [], "no error, no policy violation");
  } finally {
    await phone.close();
  }
});
