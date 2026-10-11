import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const frontend = fileURLToPath(new URL("../", import.meta.url));
const scripts = JSON.parse(readFileSync(join(frontend, "package.json"), "utf8")).scripts;

function report(source: string) {
  const folder = mkdtempSync(join(tmpdir(), "codealta-test-report-"));
  try {
    const file = join(folder, "fixture.test.mjs");
    writeFileSync(file, `import test from 'node:test'; import assert from 'node:assert/strict';\n${source}`);
    // A new runner, not another worker of the runner that executes this regression.
    const { NODE_TEST_CONTEXT: _context, ...env } = process.env;
    const configured = /--test-reporter=(\S+)/.exec(scripts.test)?.[1];
    const reporter = configured ?? "spec";
    const result = spawnSync(process.execPath, ["--test", `--test-reporter=${reporter}`, file],
      { cwd: frontend, encoding: "utf8", env, timeout: 15_000 });
    assert.ifError(result.error);
    return { status: result.status, output: result.stdout + result.stderr };
  } finally { rmSync(folder, { recursive: true, force: true }); }
}

test("normal frontend commands suppress successful test and asset listings", () => {
  assert.match(scripts.test, /--test-reporter=\.\/scripts\/failures-reporter\.mjs/);
  assert.match(scripts["test:verbose"], /--test-reporter=spec/);
  assert.match(scripts.build, /vite build --logLevel warn/);
  assert.match(scripts["build:demo"], /--logLevel warn/);
});

test("the default reporter keeps totals but omits passed, skipped and todo test names", () => {
  const result = report(`
    test('passing sentinel', () => {});
    test.skip('skipped sentinel', () => {});
    test('todo sentinel', { todo: true }, () => assert.fail('expected todo failure'));
  `);
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /passing sentinel|skipped sentinel|todo sentinel|expected todo failure/);
  assert.match(result.output, /tests 3/);
  assert.match(result.output, /pass 1/);
  assert.match(result.output, /skipped 1/);
  assert.match(result.output, /todo 1/);
});

test("the default reporter preserves nested failures, locations, assertions and emitted diagnostics", () => {
  const result = report(`
    test('parent failure', async t => {
      await t.test('passing sibling sentinel', () => {});
      await t.test('failing child', () => {
        console.error('failure diagnostic sentinel');
        assert.equal('actual sentinel', 'expected sentinel');
      });
    });
  `);
  assert.equal(result.status, 1, result.output);
  assert.doesNotMatch(result.output, /passing sibling sentinel/);
  for (const detail of ['parent failure', 'failing child', 'fixture.test.mjs', 'actual sentinel', 'expected sentinel', 'failure diagnostic sentinel'])
    assert.ok(result.output.includes(detail), `Missing ${detail}: ${result.output}`);
});

test("the default reporter does not discard output or diagnostics from passing tests", () => {
  const result = report(`
    test('passing output sentinel', t => {
      console.log('stdout sentinel');
      console.warn('warning sentinel');
      t.diagnostic('diagnostic sentinel');
    });
  `);
  assert.equal(result.status, 0, result.output);
  assert.doesNotMatch(result.output, /passing output sentinel/);
  for (const detail of ['stdout sentinel', 'warning sentinel', 'diagnostic sentinel'])
    assert.ok(result.output.includes(detail), `Missing ${detail}: ${result.output}`);
});

test("the default reporter preserves module crashes and cancelled tests as failures", () => {
  for (const source of ["throw new Error('module crash sentinel');", `
    const controller = new AbortController();
    const pending = test('cancelled sentinel', { signal: controller.signal }, () => new Promise(() => {}));
    controller.abort();
    await pending;
  `]) {
    const result = report(source);
    assert.equal(result.status, 1, result.output);
    assert.match(result.output, /module crash sentinel|cancelled sentinel/);
    assert.match(result.output, /fixture.test.mjs/);
  }
});
