import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const options = { entry: "../secrets/secretsSettings.mount.tsx", fixture: "secretsFixture", rpcFixture: "../demo-api.ts", styles: ["../secrets/secrets.css"],
  width: 1300, height: 900, reducedMotion: true } as const;
// What each row of a list shows of its name, its reference and when it was set: cut or whole, and the name's tooltip.
// The secrets and the variables; a use that waits for a confirmation is a row of its own card.
const rows = `[...document.querySelectorAll('.secret-row:not(.secret-pending-row)')].map(row => {
  const cut = element => !!element && element.scrollWidth > element.clientWidth + 1;
  const code = row.querySelector('.secret-name code');
  return { name: code.textContent, nameCut: cut(code), referenceCut: cut(row.querySelector('.secret-reference')), metaCut: cut(row.querySelector('.secret-meta')),
    title: code.title, overflow: row.scrollWidth > row.clientWidth + 1, tagCut: cut(row.querySelector('.bp6-tag .bp6-text-overflow-ellipsis') ?? row.querySelector('.bp6-tag')) };
})`;

test("the names of secrets and variables keep their room in a large window, and a narrow one cuts them with the whole name on hover", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelectorAll('.secret-row:not(.secret-pending-row)').length === 6`, "the secrets and the variables");
      await page.shot("secrets-large");
      // The page of a window of 1600 by 1000: every name, reference and date whole.
      const large = await page.evaluate(rows) as { name: string; nameCut: boolean; referenceCut: boolean; metaCut: boolean; title: string; overflow: boolean;
        tagCut: boolean }[];
      assert.deepEqual(large.filter(row => row.nameCut || row.referenceCut || row.metaCut || row.overflow).map(row => row.name), [], "nothing is cut");
      assert.deepEqual(large.map(row => row.title), large.map(row => row.name), "each name has its tooltip");

      // A window of 900 by 650: a long name may be cut, never the row, and its tooltip gives it whole.
      await page.evaluate(`window.secretsFixture.setWidth(430)`);
      await page.until(`document.querySelector('.secrets-settings').getBoundingClientRect().width < 450`, "the narrow page");
      await page.shot("secrets-narrow");
      const narrow = await page.evaluate(rows) as typeof large;
      assert.deepEqual(narrow.filter(row => row.overflow).map(row => row.name), [], "no row overflows");
      assert.deepEqual(narrow.filter(row => row.tagCut || row.metaCut).map(row => row.name), [], "the version and when it was set stay whole, the date on a line of its own");
      assert.equal(narrow.find(row => row.name === "OPENAI_API_KEY")?.nameCut, false, "a short name stays whole");
      assert.deepEqual(page.problems, []);
    }, options);
  });