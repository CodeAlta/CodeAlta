import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build, type Plugin } from "esbuild";
import { edge, withCanvas } from "../statistics/browserHarness";

// The prompt of the preview bundles the code editor of the large prompt window: its worker is built into the page.
const editorWorker: Plugin = { name: "editor-worker", setup(bundle) {
  bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
  bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
    const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
      bundle: true, platform: "browser", format: "iife", write: false });
    return { loader: "js", contents: `export default class extends Worker { constructor() {
      const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
      super(url); URL.revokeObjectURL(url);
    } }` };
  });
} };
// The preview of a Claude Code conversation in headless Edge, under the production content security policy.
const options = { entry: "../claudeCodeImport/claudeCodePreview.mount.tsx", fixture: "preview", rpcFixture: "../demo-api.ts",
  styles: ["../claudeCodeImport/claudeCodeSessions.css"], width: 1600, height: 1000, plugins: [editorWorker] } as const;
const timelineText = `document.querySelector('.claude-code-preview .timeline-scroll').textContent`;

test("the preview of a Claude Code conversation says what it is, shows the latest turn then older ones on demand, and its prompt continues it", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
      const A = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11", B = "1f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e12", C = "2f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e13";
      // The banner: where it was started, not in CodeAlta yet, and the process that works in it now.
      await page.until("window.preview && document.querySelector('.claude-code-preview-banner')", "the preview is shown");
      assert.equal(await page.evaluate("document.querySelector('.claude-code-preview-text p').textContent"),
        "Claude Code conversation started in VS Code, not in CodeAlta yet. Your first message continues it here.");
      await page.until("!!document.querySelector('.claude-code-preview-held')", "the holder is said");
      assert.equal(await page.evaluate("document.querySelector('.claude-code-preview-held').textContent"), "In use in VS Code now: your first message will ask whether to wait until it is free.");
      assert.deepEqual(await page.evaluate("preview.state.conversations"), [A], "who holds it is asked of the host");
      assert.deepEqual(await page.evaluate("[...document.querySelectorAll('.claude-code-preview-actions button')].map(button => button.textContent.trim())"), ["Continue in CodeAlta", "Open in VS Code"]);

      // The latest turn first; the older ones when asked for.
      await page.until(`${timelineText}.includes('Prompt of turn 4')`, "the latest turn is shown");
      assert.equal(await page.evaluate(`${timelineText}.includes('Prompt of turn 3')`), false, "only the latest turn at first");
      assert.deepEqual(await page.evaluate("preview.state.reads"), [{ cliSessionId: A, offset: null }], "one page, from the end");
      await page.until("!!document.querySelector('.claude-code-preview .load-more')", "older turns can be read");
      await page.evaluate("document.querySelector('.claude-code-preview .load-more').click()");
      await page.until(`${timelineText}.includes('Prompt of turn 1')`, "the older turns are read");
      assert.deepEqual(await page.evaluate("preview.state.reads.map(read => read.offset)"), [null, "91", "41"], "the pages before, from the first record of the latest turn");

      // The prompt says what it continues; its first message goes to the window, which imports then sends it.
      const placeholder = "document.querySelector('.claude-code-preview .composer-region [data-placeholder]')?.dataset.placeholder ?? null";
      assert.equal(await page.evaluate(placeholder), "Continue this VS Code conversation in CodeAlta…");
      await page.evaluate("document.querySelector('.claude-code-preview #catalog-prompt').focus()");
      await page.type("Run the tests.");
      await page.until(`preview.drafts.peek(${JSON.stringify(`claude-code-preview:${JSON.stringify(["p1", A])}`)})?.text === "Run the tests."`, "the draft is kept");
      await page.key("Enter", 13, "\r");
      await page.until("preview.state.sent.length === 1", "Enter sends the first message");
      assert.deepEqual(await page.evaluate("[preview.state.sent[0].text, preview.state.sent[0].images.length, preview.state.sent[0].scope]"),
        ["Run the tests.", 0, `claude-code-preview:${JSON.stringify(["p1", A])}`]);
      await page.evaluate("document.querySelector('.claude-code-preview-actions button:first-child').click()");
      await page.evaluate("document.querySelector('.claude-code-preview-actions button:last-child').click()");
      assert.deepEqual(await page.evaluate("[preview.state.continued, preview.state.openedInVsCode]"), [1, 1]);
      // While it is imported, nothing else is asked.
      await page.evaluate("preview.busy(true)");
      await page.until("[...document.querySelectorAll('.claude-code-preview-actions button')].every(button => button.disabled)", "the buttons wait for the import");
      assert.equal(await page.evaluate("document.querySelector('.claude-code-preview .composer-region [aria-label=\"Send\"]').disabled"), true);
      assert.match(await page.evaluate<string>("document.querySelector('.claude-code-preview .composer-region').textContent"), /Importing…/);
      await page.evaluate("preview.busy(false)");

      // Both themes and a narrow window: the banner and its buttons fit.
      for (const theme of ["dark", "light"]) {
        await page.evaluate(`preview.setTheme(${JSON.stringify(theme)})`);
        for (const [width, height] of [[1600, 1000], [900, 650]]) {
          await page.resize(width, height);
          const shown = await page.evaluate<Record<string, unknown>>(`(() => { const banner = document.querySelector('.claude-code-preview-banner').getBoundingClientRect();
            const pane = document.querySelector('.claude-code-preview').getBoundingClientRect();
            const buttons = [...document.querySelectorAll('.claude-code-preview-actions button')].map(button => button.getBoundingClientRect());
            return { overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth, fits: banner.right <= pane.right + 0.5 && banner.left >= pane.left - 0.5,
              buttons: buttons.every(button => button.right <= banner.right + 0.5 && button.width > 0) }; })()`);
          assert.deepEqual(shown, { overflow: false, fits: true, buttons: true }, `${theme} ${width}`);
        }
      }
      await page.resize(1600, 1000);

      // The next preview takes the place of this one: a conversation of a terminal that nothing holds.
      await page.evaluate("preview.state.held = null");
      await page.evaluate(`preview.show("${B}", "terminal")`);
      await page.until(`preview.state.reads.some(read => read.cliSessionId === "${B}")`, "the other conversation is read");
      await page.until(`document.querySelector('.claude-code-preview').dataset.claudeCodePreview === "${B}"`, "it is shown");
      assert.equal(await page.evaluate("document.querySelector('.claude-code-preview-text p').textContent"),
        "Claude Code conversation started in a terminal, not in CodeAlta yet. Your first message continues it here.");
      assert.equal(await page.evaluate(placeholder), "Continue this terminal conversation in CodeAlta…", "each conversation has its own draft: this one is empty");
      await page.until(`preview.state.conversations.includes("${B}")`, "who holds it is asked");
      await new Promise(resolve => setTimeout(resolve, 100));
      assert.equal(await page.evaluate("!!document.querySelector('.claude-code-preview-held')"), false, "nothing holds it: nothing is said");

      // A conversation too large to preview says so; one a session continues already opens that session.
      await page.evaluate(`preview.state.answer["${C}"] = "too_large"; preview.show("${C}")`);
      await page.until("!!document.querySelector('.claude-code-preview-unavailable')", "the preview says why it is not shown");
      assert.match(await page.evaluate<string>("document.querySelector('.claude-code-preview-unavailable').textContent"), /too large to preview/);
      await page.evaluate(`preview.state.answer["${C}"] = "existing"; preview.show("${B}")`);
      await page.evaluate(`preview.show("${C}")`);
      await page.until("preview.state.existing.length === 1", "the session that continues it is opened");
      assert.deepEqual(await page.evaluate("preview.state.existing"), ["s-old"]);
      assert.equal(await page.evaluate("document.querySelector('.claude-code-preview-continued')?.textContent.includes('A session of CodeAlta continues this conversation now.')"), true,
        "the preview says so, not that its history could not be read");

      // In the language of the window; the draft typed under the first preview is still there when it is shown again.
      await page.evaluate(`preview.show("${B}", "terminal")`);
      await page.evaluate("preview.language('de')");
      await page.until("document.querySelector('.claude-code-preview-text p')?.textContent.startsWith('In einem Terminal gestartete')", "the banner is in German");
      assert.equal(await page.evaluate(placeholder), "Diese Terminal-Unterhaltung in CodeAlta fortsetzen…");
      await page.evaluate(`preview.show("${A}")`);
      await page.until(`document.querySelector('.claude-code-preview').dataset.claudeCodePreview === "${A}"`, "the first preview is shown again");
      assert.equal(await page.evaluate(placeholder), null, "its prompt holds its draft");
      assert.equal(await page.evaluate(`preview.drafts.peek(${JSON.stringify(`claude-code-preview:${JSON.stringify(["p1", A])}`)})?.text`), "Run the tests.");
  }, options);
});

test("the preview reads a conversation again as it grows, recovers from a failed read, keeps a message no session took, and waits while another is imported", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    const A = "0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11", D = "3f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e14", E = "4f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e15";
    const scope = (id: string) => JSON.stringify(`claude-code-preview:${JSON.stringify(["p1", id])}`);
    await page.until(`window.preview && ${timelineText}.includes('Prompt of turn 4')`, "the latest turn is shown");

    // VS Code adds a turn to the conversation: the preview reads its newest page again, and shows it.
    await page.evaluate(`preview.refresh(200); preview.state.turns["${A}"] = 5`);
    await page.until(`${timelineText}.includes('Prompt of turn 5')`, "the new turn is shown");

    // A first read that fails is tried again until it is read.
    await page.evaluate(`preview.state.answer["${D}"] = "read_failed"; preview.show("${D}")`);
    await page.until(`preview.state.reads.filter(read => read.cliSessionId === "${D}").length >= 2`, "the failed read is tried again");
    await page.evaluate(`delete preview.state.answer["${D}"]`);
    await page.until(`${timelineText}.includes('Prompt of turn 4')`, "the preview recovers");

    // While another conversation is imported, nothing is asked here, and the prompt says why.
    await page.evaluate("preview.importingElsewhere(true)");
    await page.until("[...document.querySelectorAll('.claude-code-preview-actions button')].every(button => button.disabled)", "the buttons wait for the other import");
    assert.equal(await page.evaluate("document.querySelector('.claude-code-preview .composer-region [aria-label=\"Send\"]').disabled"), true);
    assert.match(await page.evaluate<string>("document.querySelector('.claude-code-preview .composer-region').textContent"), /Another conversation is being imported…/);
    await page.evaluate("preview.importingElsewhere(false)");
    await page.until("[...document.querySelectorAll('.claude-code-preview-actions button')].every(button => !button.disabled)", "the buttons can be used again");

    // A session continues the conversation now, but a message waits under the preview: the preview stays, with it.
    await page.evaluate("document.querySelector('.claude-code-preview #catalog-prompt').focus()");
    await page.type("Look at this.");
    await page.until(`preview.drafts.peek(${scope(D)})?.text === "Look at this."`, "the draft is kept");
    await page.evaluate(`preview.state.answer["${D}"] = "existing"`);
    await page.until("!!document.querySelector('.claude-code-preview-continued')", "the preview says a session continues the conversation");
    assert.match(await page.evaluate<string>("document.querySelector('.claude-code-preview-continued').textContent"), /Your message stayed here/);
    assert.deepEqual(await page.evaluate("preview.state.existing"), [], "the preview is not left for that session");
    assert.equal(await page.evaluate(`preview.drafts.peek(${scope(D)})?.text`), "Look at this.");
    await page.evaluate("document.querySelector('.claude-code-preview-continued button').click()");
    assert.deepEqual(await page.evaluate("preview.state.existing"), ["s-old:kept"], "the session is opened beside the preview, which keeps the message");

    // Without a message, the preview leaves for that session.
    await page.evaluate(`preview.state.answer["${E}"] = "existing"; preview.show("${E}")`);
    await page.until("preview.state.existing.length === 2", "the session that continues it is opened");
    assert.deepEqual(await page.evaluate("preview.state.existing"), ["s-old:kept", "s-old"]);
  }, options);
});
