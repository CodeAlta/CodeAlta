import assert from "node:assert/strict";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build, type Plugin } from "esbuild";
import { edge, withCanvas, type Page } from "./statistics/browserHarness";

// The Markdown instruction uses the real code editor; bundle its worker as the other editor fixtures do.
const editorWorker: Plugin = { name: "editor-worker", setup(bundle) {
  bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
  bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
    const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
      bundle: true, platform: "browser", format: "iife", write: false });
    return { loader: "js", contents: `export default class extends Worker { constructor() {
      const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
      super(url); URL.revokeObjectURL(url);
    } }` };
  });
} };
const options = { entry: "../sessionAttribution.mount.tsx", fixture: "attributionFixture", rpcFixture: "../demo-api.ts",
  width: 1300, height: 1200, plugins: [editorWorker] } as const;
const trailer = `document.querySelector('#settings-commit-co-author-trailer')`;
const instruction = ".attribution-markdown .monaco-editor textarea";
const saved = `JSON.stringify(attributionFixture.saved.map(request => Object.fromEntries(Object.entries(request).filter(([key, value]) => key !== 'expectedEpoch' && value !== null))))`;
const selectAll = async (page: Page) => {
  const modifiers = process.platform === "darwin" ? 4 : 2;
  await page.command("Input.dispatchKeyEvent", { type: "keyDown", key: "a", code: "KeyA", windowsVirtualKeyCode: 65, modifiers });
  await page.command("Input.dispatchKeyEvent", { type: "keyUp", key: "a", code: "KeyA", windowsVirtualKeyCode: 65, modifiers });
};

test("the texts of attribution show the ones of CodeAlta until the user writes another, and each choice is saved alone", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`!!${trailer} && !!document.querySelector('${instruction}')`, "the trailer and the Markdown editor");
    // The texts agents get are filled in, so the user can tweak them instead of writing them again.
    assert.deepEqual(await page.evaluate(`[${trailer}.value, ${trailer}.placeholder]`),
      ["Co-authored-by: CodeAlta <noreply@codealta.io>", "Co-authored-by: CodeAlta <noreply@codealta.io>"]);
    await page.until(`document.querySelector('.attribution-markdown .view-lines').textContent.replace(/\\s+/g, ' ').includes('When you write')`, "the default instruction");

    // Removing CodeAlta asks first, with Keep it as the safe choice and no premature save.
    await page.click("#settings-commit-co-author-trailer");
    await selectAll(page);
    await page.type("Assisted-by: Contoso <agents@contoso.example>");
    await page.key("Enter", 13);
    await page.until(`!!document.querySelector('.attribution-confirmation')`, "the confirmation before saving");
    assert.equal(await page.evaluate(`attributionFixture.saved.length`), 0);
    await page.clickText(".attribution-confirmation button", "Keep it");
    await page.until(`${trailer}.value === 'Co-authored-by: CodeAlta <noreply@codealta.io>'`, "the original trailer kept");

    // A trailer the user confirms, saved on Enter: nothing else is sent.
    await page.click("#settings-commit-co-author-trailer");
    await selectAll(page);
    await page.type("Assisted-by: Contoso <agents@contoso.example>");
    await page.key("Enter", 13);
    await page.clickText(".attribution-confirmation button", "Save it anyway");
    await page.until(`attributionFixture.saved.length === 1 && attributionFixture.held.coAuthorTrailer.startsWith('Assisted-by')`, "the trailer saved");
    assert.equal(await page.evaluate(saved), JSON.stringify([{ coAuthorTrailer: "Assisted-by: Contoso <agents@contoso.example>" }]));
    await page.until(`${trailer}.value === 'Assisted-by: Contoso <agents@contoso.example>' && !${trailer}.disabled`, "the trailer of the user shown");

    // What the host refuses stays in the field, with the reason.
    await page.evaluate(`${trailer}.select()`);
    await page.type("CodeAlta helped");
    await page.key("Enter", 13);
    await page.until(`document.body.textContent.includes('The values were not accepted.') && document.body.textContent.includes('is a Git trailer')`, "the refusal and its reason");
    assert.equal(await page.evaluate(`${trailer}.value`), "CodeAlta helped");
    assert.equal(await page.evaluate(`attributionFixture.held.coAuthorTrailer`), "Assisted-by: Contoso <agents@contoso.example>");

    // Clearing the field restores the text of CodeAlta, with no custom text saved.
    await page.evaluate(`${trailer}.select()`);
    await page.key("Backspace", 8);
    await page.key("Enter", 13);
    await page.until(`attributionFixture.saved.length === 3 && attributionFixture.held.coAuthorTrailer === 'Co-authored-by: CodeAlta <noreply@codealta.io>'`, "the trailer of CodeAlta again");
    await page.until(`${trailer}.value === 'Co-authored-by: CodeAlta <noreply@codealta.io>'`, "the default text filled in again");

    // The Markdown editor accepts line breaks; Save stores a single line, after confirmation.
    await page.click(".attribution-markdown .view-lines");
    await selectAll(page);
    await page.command("Input.insertText", { text: "End it with _Posted by an agent._\nThank you." });
    await page.until(`!document.querySelector('.attribution-markdown .view-lines').textContent.includes('CodeAlta')`, "the instruction replaced");
    await page.clickText(".attribution-markdown-actions button", "Save");
    await page.clickText(".attribution-confirmation button", "Save it anyway");
    await page.until(`attributionFixture.held.externalWritingInstruction === 'End it with _Posted by an agent._ Thank you.'`, "the instruction saved on one line");

    // Turning the switch off also asks first; the instruction is kept, and its editor is read-only.
    await page.click("#settings-external-writing-attribution + .bp6-control-indicator");
    await page.clickText(".attribution-confirmation button", "Turn it off");
    await page.until(`attributionFixture.held.externalWriting === false && [...document.querySelectorAll('.attribution-markdown-actions button')].every(button => button.disabled)`, "the attribution of external writing off");
    await page.click(".attribution-markdown .view-lines");
    await page.command("Input.insertText", { text: "not written" });
    assert.equal(await page.evaluate(`document.querySelector('.attribution-markdown .view-lines').textContent.includes('not written')`), false);
    assert.equal(await page.evaluate(`attributionFixture.held.externalWritingInstruction`), "End it with _Posted by an agent._ Thank you.");
    assert.equal(await page.evaluate(saved), JSON.stringify([{ coAuthorTrailer: "Assisted-by: Contoso <agents@contoso.example>" }, { coAuthorTrailer: "CodeAlta helped" },
      { coAuthorTrailer: "" }, { externalWritingInstruction: "End it with _Posted by an agent._ Thank you." }, { externalWriting: false }]));
    assert.deepEqual(page.problems, []);
  }, options);
});
