import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const ask = (selector: string) => `document.querySelector(${JSON.stringify(selector)})`;
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

test("a vault item is found by its name in any vault, its hidden field is picked and the secret is named after it", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      // The reference of a secret is copied by a click, to be pasted in a configuration.
      await page.until(`document.querySelectorAll(".secret-reference-copy").length > 0`, "the references");
      await page.evaluate(`Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async text => { window.copiedText = text; } } })`);
      await page.click('.secret-row[data-secret="OPENAI_API_KEY"] .secret-reference-copy');
      await page.until(`window.copiedText === "\${secret:OPENAI_API_KEY}" && document.querySelector('.secret-row[data-secret="OPENAI_API_KEY"] .secret-reference-copy').dataset.copied === "true"`, "the reference copied");

      await page.clickText(".secrets-settings button", "Add from a vault…");
      await page.until(`${ask(".vault-browser-search input")} && document.querySelectorAll(".vault-browser-entry").length === 2`, "the vaults");
      // The search field has the focus: what is typed looks into every vault.
      await page.type("git");
      await page.until(`[...document.querySelectorAll(".vault-browser-entry")].map(e => e.textContent).join("|") === "Private › GitHublogin"`, "GitHub found in Private");
      await page.key("Enter", 13);
      await page.until(`${ask(".vault-browser-picked")}?.textContent === "Private › GitHub › password"`, "the password picked by itself");
      assert.equal(await page.evaluate(`${ask("#vault-binding-name")}.value`), "GITHUB_PASSWORD", "the secret is named after the item and its field");
      assert.equal(await page.evaluate(`[...document.querySelectorAll(".vault-binding-dialog .bp6-dialog-footer button")].find(b => b.textContent === "Use this item").disabled`), false);
      await page.shot("secrets-vault-search");

      // A word found nowhere says so; a name typed by the user is no longer changed.
      await page.clickText(".vault-browser-trail button", "1Password");
      await page.until(`document.querySelectorAll(".vault-browser-entry").length === 2`, "the top again");
      await page.click(".vault-browser-search input");
      await page.type("nothing");
      await page.until(`${ask(".vault-browser-none")}?.textContent === "Nothing in the vault has this name."`, "no match");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a picker closed while its vault is still listing aborts the call, so that nothing stays queued for the vault", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelectorAll(".secret-row").length > 0`, "the secrets");
      await page.evaluate(`window.secretsFixture.control.slow = true`);
      await page.clickText(".secrets-settings button", "Add from a vault…");
      await page.until(`window.secretsFixture.browses.some(call => call.parent === null)`, "the top of the vault asked");
      await page.clickText(".vault-binding-dialog .bp6-dialog-footer button", "Cancel");
      await page.until(`!document.querySelector(".vault-binding-dialog")`, "the picker closed");
      await page.until(`window.secretsFixture.browses.filter(call => call.parent === null).every(call => call.aborted)`, "the list it asked is aborted");
      assert.deepEqual(page.problems, []);
    }, options);
  });
