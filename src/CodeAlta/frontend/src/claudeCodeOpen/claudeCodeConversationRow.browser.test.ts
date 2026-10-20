import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";

// The row of the Session Info window that names the conversation of the Claude Code CLI a session continues, in headless Edge
// under React StrictMode: read once when the window opens, absent for a session that has none, and its identifier copied.

const options = { entry: "../claudeCodeOpen/claudeCodeConversationRow.mount.tsx", fixture: "conversationFixture", rpcFixture: "../demo-api.ts", width: 900, height: 800 } as const;
const row = `document.querySelector('.claude-code-conversation-row')`;

test("Session Info names the Claude Code conversation of the session, and nothing for a session that has none", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    // A session of another provider, or before its first turn: the window gives no reader, and the row is not there.
    await page.evaluate(`conversationFixture.render(false)`);
    await page.until(`document.querySelector('.session-info-dialog')`, "the Session Info window");
    assert.equal(await page.evaluate(`!!${row}`), false);
    assert.equal(await page.evaluate(`conversationFixture.state.reads`), 0);

    // The host has none for it (another provider): still no row.
    await page.evaluate(`conversationFixture.clear(); conversationFixture.render(true)`);
    await page.until(`conversationFixture.state.reads > 0`, "the conversation read");
    assert.equal(await page.evaluate(`!!${row}`), false);

    // A Claude Code session with its conversation: the row, under the provider.
    await page.evaluate(`conversationFixture.clear(); conversationFixture.state.reads = 0; conversationFixture.state.answer = conversationFixture.conversation(); conversationFixture.render(true)`);
    await page.until(`!!${row}`, "the row of the conversation");
    assert.deepEqual(await page.evaluate(`[${row}.querySelector('dt').textContent, ${row}.querySelector('code').textContent]`),
      ["Claude Code conversation", "5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01"]);
    assert.equal(await page.evaluate(`${row}.previousElementSibling.querySelector('dt').textContent`), "Provider");
    assert.ok(await page.evaluate<number>(`conversationFixture.state.reads`) <= 2, "read when the window opens, not polled");
  }, options);
});

test("the identifier of the conversation is copied, and another program that holds it is named", { skip: !edge, timeout: 240_000 }, async () => {
  await withCanvas(async page => {
    await page.evaluate(`conversationFixture.state.answer = conversationFixture.conversation({ origin: "vsCode", originText: "VS Code", processId: 4242,
      entrypoint: "claude-vscode", kind: "interactive", status: "idle", workingDirectory: null, name: null }); conversationFixture.renderRow()`);
    await page.until(`!!${row}`, "the row of the conversation");
    assert.equal(await page.evaluate(`${row}.querySelector('small').textContent`), "Open now in VS Code");
    await page.click(`.claude-code-conversation-row button`);
    await page.until(`conversationFixture.state.copied.length === 1`, "the copy");
    assert.deepEqual(await page.evaluate(`conversationFixture.state.copied`), ["5b0c8b0e-3f7c-4c1e-9a55-6d1f0b2c3e01"]);
    await page.until(`${row}.querySelector('button').textContent.trim() === 'Copied'`, "the button says it was copied");
  }, options);
});
