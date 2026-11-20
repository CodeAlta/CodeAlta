import assert from "node:assert/strict";
import test from "node:test";
import { edge, withCanvas, type Page } from "../statistics/browserHarness";
import { testLimit } from "../testTime";

const ask = (selector: string) => `document.querySelector(${JSON.stringify(selector)})`;
const options = { entry: "../secrets/secretsSettings.mount.tsx", fixture: "secretsFixture", rpcFixture: "../demo-api.ts",
  styles: ["../spaces/spaces.css", "../fleet/console/fleetConsole.css", "../secrets/secrets.css"], width: 1300, height: 900, reducedMotion: true } as const;
// What each row of the table of a tab shows of its name: cut or whole, its tooltip, and the columns a narrow page leaves out.
const rows = (tab: string) => `[...document.querySelectorAll('[data-secrets-panel="${tab}"] tr[data-row-key]')].map(row => {
  const cut = element => !!element && element.scrollWidth > element.clientWidth + 1;
  const code = row.querySelector('.secret-cell-name code');
  return { name: code.textContent, nameCut: cut(code), title: code.title, hidden: [...row.querySelectorAll('td[data-secondary]')].every(cell => getComputedStyle(cell).display === 'none'),
    shown: [...row.querySelectorAll('td[data-secondary]')].every(cell => getComputedStyle(cell).display !== 'none') };
})`;
// Whether the page, or what the selector names, is wider than its room: a table scrolls in its own frame instead.
const overflows = (selector: string) => `(() => { const element = ${ask(selector)}; return element.scrollWidth > element.clientWidth + 1; })()`;
type Row = { name: string; nameCut: boolean; title: string; hidden: boolean; shown: boolean };

/** Opens a tab by its button, and waits for its panel. */
async function openTab(page: Page, tab: string) {
  await page.until(`${ask(`[data-secrets-tab="${tab}"]`)}`, `the tab ${tab}`);
  await page.click(`[data-secrets-tab="${tab}"]`);
  await page.until(`${ask(`[data-secrets-panel="${tab}"]`)}`, `the panel of ${tab}`);
}

test("the page opens on an overview, its tabs say what they hold, and its tables keep their room in a large window and a narrow one", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      // The overview: a card per concept, and the use that waits for a confirmation above the tabs.
      await page.until(`document.querySelectorAll('.secrets-overview-card').length === 5`, "the cards of the overview");
      assert.equal(await page.evaluate(`!!${ask(".secret-pending-row")} && ${ask(".secret-pending")}.compareDocumentPosition(${ask(".secrets-tabs")}) === Node.DOCUMENT_POSITION_FOLLOWING`), true,
        "what waits for a confirmation stays above the tabs");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('.secrets-tabs [role=tab]')].map(tab => [...tab.querySelectorAll('.bp6-button-text > span')].map(part => part.textContent).join(': '))`),
        ["Overview", "Secrets: 4 · 1 locked", "Variables: 2", "Environment: 2 projects", "Vaults: 3", "Sign-ins: 1", "Activity: 1 recent"]);
      assert.match(await page.evaluate(`${ask('[data-overview="secrets"] .secrets-overview-attention')}.textContent`) as string, /1 locked · ACME_GH_TOKEN is not set/);

      // A card opens its tab; Left and Right move between the tabs.
      await page.click('[data-overview="variables"] .secrets-overview-open');
      await page.until(`${ask('[data-secrets-panel="variables"]')}`, "the variables, from their card");
      await page.evaluate(`${ask('[data-secrets-tab="variables"]')}.focus()`);
      await page.key("ArrowLeft", 37);
      await page.until(`${ask('[data-secrets-panel="secrets"]')} && document.activeElement?.dataset.secretsTab === "secrets"`, "the secrets, by the keyboard");

      // The page of a window of 1600 by 1000: every name whole, every column shown, nothing wider than the page.
      await page.until(`document.querySelectorAll('[data-secrets-panel="secrets"] tr[data-row-key]').length === 4`, "the secrets");
      const large = await page.evaluate(rows("secrets")) as Row[];
      assert.deepEqual(large.filter(row => row.nameCut || !row.shown).map(row => row.name), [], "nothing is cut nor left out");
      assert.deepEqual(large.map(row => row.title), large.map(row => row.name), "each name has its tooltip");
      assert.equal(await page.evaluate(overflows(".secrets-settings")), false, "the page is not wider than its window");

      // A window of 900 by 650: the secondary columns are in the details, the table scrolls in its frame, and the page never does.
      await page.evaluate(`window.secretsFixture.setWidth(430)`);
      await page.until(`document.querySelector('.secrets-settings').getBoundingClientRect().width < 450`, "the narrow page");
      await page.until(`getComputedStyle(${ask('[data-secrets-panel="secrets"] th[data-secondary]')}).display === "none"`, "the secondary columns left out");
      const narrow = await page.evaluate(rows("secrets")) as Row[];
      assert.deepEqual(narrow.filter(row => !row.hidden).map(row => row.name), [], "a narrow row keeps what matters");
      assert.equal(await page.evaluate(overflows(".secrets-settings")), false, "the page is not wider than its window");
      assert.equal(await page.evaluate(`(() => { const nav = ${ask(".secrets-tabs")}; return nav.scrollWidth <= nav.clientWidth + 1
        && [...nav.children].every(tab => tab.getBoundingClientRect().right <= nav.getBoundingClientRect().right + 1); })()`), true, "the tabs wrap onto rows");
      for (const tab of ["variables", "environment", "vaults", "signIns", "activity", "overview"]) {
        await openTab(page, tab);
        assert.equal(await page.evaluate(overflows(".secrets-settings")), false, `${tab}: the page is not wider than its window`);
      }
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a secret is limited to projects in its details, and a link opens a tab", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await page.until(`document.querySelectorAll('.secrets-overview-card').length === 5`, "the overview");
      // As \`alta settings open secrets.list\` does.
      await page.evaluate(`window.secretsFixture.open("secrets")`);
      await page.until(`document.querySelectorAll('[data-secrets-panel="secrets"] tr[data-row-key]').length === 4`, "the secrets, by a link");
      assert.equal(await page.evaluate(`${ask('[data-secrets-panel="secrets"]')}.dataset.setting`), "secrets.list", "the setting that glows");

      // The filter finds a secret by its project.
      await page.click(".secrets-filter input");
      await page.type("acme-web");
      await page.until(`document.querySelectorAll('[data-secrets-panel="secrets"] tr[data-row-key]').length === 1`, "the secret of acme-web");
      assert.equal(await page.evaluate(`${ask('[data-secrets-panel="secrets"] tr[data-row-key]')}.dataset.secret`), "GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION_EU_WEST_1");
      await page.evaluate(`(() => { const input = ${ask(".secrets-filter input")}; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set.call(input, "");
        input.dispatchEvent(new Event("input", { bubbles: true })); })()`);
      await page.until(`document.querySelectorAll('[data-secrets-panel="secrets"] tr[data-row-key]').length === 4`, "every secret again");

      // A row opens its details: what names the secret, and its projects, each check saved at once.
      await page.click('tr[data-secret="OPENAI_API_KEY"] td[data-column="source"]');
      await page.until(`${ask('[data-secret-details="OPENAI_API_KEY"]')}`, "the details of the secret");
      assert.equal(await page.evaluate(`${ask('[data-secret-details="OPENAI_API_KEY"] [data-used-by="provider"]')}.textContent`), "openai");
      await page.clickText('[data-secret-details="OPENAI_API_KEY"] .secret-projects button', "Only these projects");
      await page.until(`${ask('[data-secret-details="OPENAI_API_KEY"] input[data-project="acme-api"]')}`, "the projects of the catalog");
      assert.deepEqual(await page.evaluate(`window.secretsFixture.calls`), [], "choosing to limit changes nothing before a project is checked");
      await page.evaluate(`${ask('[data-secret-details="OPENAI_API_KEY"] input[data-project="acme-api"]')}.click()`);
      await page.until(`window.secretsFixture.calls.length === 1`, "the projects saved");
      assert.deepEqual(await page.evaluate(`window.secretsFixture.calls`), [{ setSecretProjects: { name: "OPENAI_API_KEY", projectIds: ["acme-api"] } }]);
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("the environment of a project is edited under its row, and what it misses on this computer is set from there", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await openTab(page, "environment");
      await page.until(`document.querySelectorAll('[data-secrets-panel="environment"] tr[data-row-key]').length === 2`, "the projects that set an environment");
      assert.deepEqual(await page.evaluate(`[...document.querySelectorAll('[data-secrets-panel="environment"] tr[data-row-key]')].map(row => row.dataset.project)`), ["acme-api", "acme-web"]);
      assert.equal(await page.evaluate(`${ask('tr[data-project="acme-web"] td[data-column="state"]')}.textContent`), "1 secret not set");

      // The editor of a project: a variable added with the reference of a variable of this computer.
      await page.click('tr[data-project="acme-web"] td[data-column="state"]');
      await page.until(`document.querySelectorAll('[data-environment="acme-web"] tr[data-environment-variable]').length === 3`, "the variables of the project");
      assert.equal(await page.evaluate(`!!${ask('[data-environment-variable="NODE_ENV"] button[aria-label="Delete NODE_ENV"]')}`), false, "an inherited variable is not deleted here");
      await page.click('[data-environment="acme-web"] .environment-new input[aria-label="Name of the environment variable"]');
      await page.type("OTLP_ENDPOINT");
      await page.click('[data-environment="acme-web"] .environment-new button[aria-label="Insert a reference"]');
      await page.until(`[...document.querySelectorAll(".environment-insert .bp6-menu-item")].some(item => item.textContent === "PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING")`, "the references of this computer");
      await page.clickText(".environment-insert .bp6-menu-item", "PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING");
      await page.clickText('[data-environment="acme-web"] .environment-new button', "Add");
      await page.until(`window.secretsFixture.calls.length === 1`, "the variable saved");
      assert.deepEqual(await page.evaluate(`window.secretsFixture.calls`),
        [{ setProjectEnvironment: { projectId: "acme-web", name: "OTLP_ENDPOINT", value: "${var:PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING}" } }]);

      // The file it is saved in opens in the editor, and a space opens on its Identity.
      await page.clickText('[data-environment="acme-web"] .environment-file button', "Open");
      await page.click('[data-space="work"] button');
      assert.deepEqual(await page.evaluate(`window.secretsFixture.opened`), [{ file: "acme-web", path: "C:\\code\\acme-web\\.alta\\config.toml" }, { space: "work" }]);

      // A project without an environment is chosen to be given one.
      await page.clickText('[data-secrets-panel="environment"] button', "Set the environment of a project…");
      await page.until(`document.querySelectorAll('.environment-picker .bp6-menu-item').length === 1`, "the projects without an environment");
      await page.clickText(".environment-picker .bp6-menu-item", "Docs");
      await page.until(`${ask('[data-environment="docs"] .environment-new')}`, "the editor of the project chosen");

      // A secret a variable names that is not set here: Set it opens the form that adds it, with its name.
      await page.click('tr[data-project="acme-web"] td[data-column="state"]');
      await page.until(`${ask('[data-environment-variable="GH_TOKEN"] .environment-issue button')}`, "what the variable misses");
      await page.click('[data-environment-variable="GH_TOKEN"] .environment-issue button');
      await page.until(`${ask('.secret-new-popover input[aria-label="Name of the secret"]')}?.value === "ACME_GH_TOKEN"`, "the form of the secret, with its name");
      assert.equal(await page.evaluate(`!!${ask('[data-secrets-panel="secrets"]')}`), true, "on the tab of the secrets");
      await page.click('.secret-new-popover input[aria-label="Value of the secret"]');
      await page.type("ghp_value");
      await page.clickText(".secret-new-popover button", "Add");
      await page.until(`window.secretsFixture.calls.length === 2`, "the secret saved");
      assert.deepEqual(await page.evaluate(`Object.keys(window.secretsFixture.calls[1].setSecret)`), ["name", "value"]);
      assert.equal(await page.evaluate(`window.secretsFixture.calls[1].setSecret.name`), "ACME_GH_TOKEN");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a vault item is found by its name in any vault, its hidden field is picked and the secret is named after it", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await openTab(page, "secrets");
      // The reference of a secret is copied by a click, to be pasted in a configuration.
      await page.until(`document.querySelectorAll(".secret-reference-copy").length > 0`, "the references");
      await page.evaluate(`Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async text => { window.copiedText = text; } } })`);
      await page.click('tr[data-secret="OPENAI_API_KEY"] .secret-reference-copy');
      await page.until(`window.copiedText === "\${secret:OPENAI_API_KEY}" && document.querySelector('tr[data-secret="OPENAI_API_KEY"] .secret-reference-copy').dataset.copied === "true"`, "the reference copied");
      assert.equal(await page.evaluate(`!!${ask('[data-secret-details="OPENAI_API_KEY"]')}`), false, "copying does not open the details of the row");

      await page.clickText(".secrets-settings button", "Add from a vault…");
      await page.until(`${ask(".vault-browser-search input")} && document.querySelectorAll(".vault-browser-entry").length === 2`, "the vaults");
      // The search field has the focus: what is typed looks into every vault.
      await page.type("git");
      await page.until(`[...document.querySelectorAll(".vault-browser-entry")].map(e => e.textContent).join("|") === "Private › GitHublogin"`, "GitHub found in Private");
      await page.key("Enter", 13);
      await page.until(`${ask(".vault-browser-picked")}?.textContent === "Private › GitHub › password"`, "the password picked by itself");
      assert.equal(await page.evaluate(`${ask("#vault-binding-name")}.value`), "GITHUB_PASSWORD", "the secret is named after the item and its field");
      assert.equal(await page.evaluate(`[...document.querySelectorAll(".vault-binding-dialog .bp6-dialog-footer button")].find(b => b.textContent === "Use this item").disabled`), false);

      // A word found nowhere says so; a name typed by the user is no longer changed.
      await page.clickText(".vault-browser-trail button", "1Password");
      await page.until(`document.querySelectorAll(".vault-browser-entry").length === 2`, "the top again");
      await page.click(".vault-browser-search input");
      await page.type("nothing");
      await page.until(`${ask(".vault-browser-none")}?.textContent === "Nothing in the vault has this name."`, "no match");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a search at the top of Bitwarden matches the names of its items without opening each one", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await openTab(page, "secrets");
      await page.clickText(".secrets-settings button", "Add from a vault…");
      await page.until(`${ask("#vault-binding-provider")}`, "the choice of the vault");
      await page.evaluate(`(() => { const select = ${ask("#vault-binding-provider")}; select.value = "vault"; select.dispatchEvent(new Event("change", { bubbles: true })); })()`);
      await page.until(`[...document.querySelectorAll(".vault-browser-entry")].map(e => e.textContent).join("|") === "GitHublogin|Server notesnote"`, "the items of Bitwarden");
      await page.click(".vault-browser-search input");
      await page.type("git");
      await page.until(`[...document.querySelectorAll(".vault-browser-entry")].map(e => e.textContent).join("|") === "GitHublogin"`, "GitHub found by its name");
      assert.equal(await page.evaluate(`!!${ask(".vault-browser-reading")}`), false, "nothing is still being searched");
      assert.deepEqual(await page.evaluate(`window.secretsFixture.browses.filter(call => call.id === "vault" && call.parent !== null).map(call => call.parent)`), [],
        "no item of Bitwarden is opened by the search");

      // The item still opens onto its fields, and its hidden field is picked.
      await page.key("Enter", 13);
      await page.until(`${ask(".vault-browser-picked")}?.textContent === "GitHub › password"`, "the password picked by itself");
      assert.deepEqual(page.problems, []);
    }, options);
  });

test("a picker closed while its vault is still listing aborts the call, so that nothing stays queued for the vault", { skip: !edge, timeout: testLimit(90_000) },
  async () => {
    await withCanvas(async page => {
      await openTab(page, "secrets");
      await page.evaluate(`window.secretsFixture.control.slow = true`);
      await page.clickText(".secrets-settings button", "Add from a vault…");
      await page.until(`window.secretsFixture.browses.some(call => call.parent === null)`, "the top of the vault asked");
      await page.clickText(".vault-binding-dialog .bp6-dialog-footer button", "Cancel");
      await page.until(`!document.querySelector(".vault-binding-dialog")`, "the picker closed");
      await page.until(`window.secretsFixture.browses.filter(call => call.parent === null).every(call => call.aborted)`, "the list it asked is aborted");
      assert.deepEqual(page.problems, []);
    }, options);
  });
