import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "./statistics/browserHarness";

const options = { entry: "../sessionAttribution.mount.tsx", fixture: "attributionFixture", rpcFixture: "../demo-api.ts", width: 1300, height: 1200 } as const;
const trailer = `document.querySelector('#settings-commit-co-author-trailer')`;
const instruction = `document.querySelector('#settings-external-writing-instruction')`;
const saved = `JSON.stringify(attributionFixture.saved.map(request => Object.fromEntries(Object.entries(request).filter(([key, value]) => key !== 'expectedEpoch' && value !== null))))`;

test("the texts of attribution show the ones of CodeAlta until the user writes another, and each choice is saved alone", { skip: !edge, timeout: 90_000 }, async () => {
  await withCanvas(async page => {
    await page.until(`!!${trailer} && !!${instruction}`, "the two texts, beside their switches");
    // The texts of CodeAlta are the placeholders of empty fields.
    assert.deepEqual(await page.evaluate(`[${trailer}.value, ${trailer}.placeholder, ${instruction}.value, ${instruction}.tagName]`),
      ["", "Co-authored-by: CodeAlta <noreply@codealta.io>", "", "TEXTAREA"]);

    // A trailer of the user, saved on Enter: nothing else is sent.
    await page.click("#settings-commit-co-author-trailer");
    await page.type("Assisted-by: Contoso <agents@contoso.example>");
    await page.key("Enter", 13);
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

    // An empty field is the text of CodeAlta again.
    await page.evaluate(`${trailer}.select()`);
    await page.key("Backspace", 8);
    await page.key("Enter", 13);
    await page.until(`attributionFixture.saved.length === 3 && attributionFixture.held.coAuthorTrailer === 'Co-authored-by: CodeAlta <noreply@codealta.io>'`, "the trailer of CodeAlta again");
    await page.until(`${trailer}.value === ''`, "the field empty");

    // The instruction stays on one line: Enter saves it, and adds no line.
    await page.click("#settings-external-writing-instruction");
    await page.type("End it with _Posted by an agent._");
    await page.key("Enter", 13);
    await page.until(`attributionFixture.held.externalWritingInstruction === 'End it with _Posted by an agent._'`, "the instruction saved");
    assert.equal(await page.evaluate(`${instruction}.value.includes('\\n')`), false);

    // Off, a text cannot be written; it is kept for when the switch is on again.
    await page.click("#settings-external-writing-attribution + .bp6-control-indicator");
    await page.until(`attributionFixture.held.externalWriting === false && ${instruction}.disabled`, "the attribution of external writing off");
    assert.equal(await page.evaluate(`${instruction}.value`), "End it with _Posted by an agent._");
    assert.equal(await page.evaluate(saved), JSON.stringify([{ coAuthorTrailer: "Assisted-by: Contoso <agents@contoso.example>" }, { coAuthorTrailer: "CodeAlta helped" },
      { coAuthorTrailer: "" }, { externalWritingInstruction: "End it with _Posted by an agent._" }, { externalWriting: false }]));
    assert.deepEqual(page.problems, []);
  }, options);
});
