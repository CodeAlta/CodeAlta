import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { readScale, testLimit, testLimitRanOut, testTimeCiScale, testTimeScale } from "./testTime";

// Keeps every time limit of the frontend tests in testTime.ts, longer on CI: it reads the tests and their helpers and
// names each line that writes a limit as a bare number. src/CodeAlta.Tests/TestTimeGuardTests.cs does the same for .NET.

const source = fileURLToPath(new URL("./", import.meta.url));
// A line that gives a time which is not a limit (what a test checks, a delay the app is given) says so.
const notALimit = "not a limit";

// The shapes of a bare limit: the timeout option of a test (or of a child process), a deadline, the timer of an awaited
// answer that rejects when it runs out, the default of a limit parameter, and the limit of a fetch.
const bareLimits: readonly RegExp[] = [
  /\btimeout\s*:\s*[1-9][\d_]*/,
  /(?<!new Date\(\s*)\b(?:Date|performance)\.now\(\)\s*\+\s*\d/,
  /\bsetTimeout\(.*\b(?:timed out|timeout|deadline)\b.*,\s*\d[\d_]*\s*\)/,
  /\b(?:timeoutMs|timeout|milliseconds)\s*(?::\s*number\s*)?=\s*\d/,
  /\bAbortSignal\.timeout\(\s*\d/,
];

const bareLimit = (line: string) => !line.includes(notALimit) && bareLimits.some(shape => shape.test(line));

// The tests, and the helpers that run beside them in Node (harnesses, fixtures, the browser and pointer helpers). A
// `.mount.tsx` runs in the page, where every time is one the app is given: a limit belongs to the test that waits.
function testSources(): string[] {
  const files: string[] = [];
  const walk = (folder: string) => {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      if (entry.isDirectory()) walk(path);
      else if (/\.tsx?$/.test(entry.name) && !/^testTime(\.test)?\.ts$/.test(entry.name) && isTestSource(entry.name, path)) files.push(path);
    }
  };
  walk(source);
  return files.sort();
}

function isTestSource(name: string, path: string): boolean {
  if (/\.mount\.tsx?$/.test(name)) return false;
  if (/\.test\.tsx?$/.test(name) || /(?:Harness|[Ff]ixture\w*|\.fixture)\.ts$/.test(name) || name === "browserPointer.ts") return true;
  return /from\s+["']node:/.test(readFileSync(path, "utf8"));
}

test("every time limit of the frontend tests is taken from testTime", () => {
  const problems: string[] = [];
  const files = testSources();
  assert.ok(files.length > 100, `the tests are found under ${source}`);
  for (const file of files) {
    const lines = readFileSync(file, "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      const code = line.trimStart();
      if (code.startsWith("//") || code.startsWith("*") || code.startsWith("/*")) return;
      if (bareLimit(line))
        problems.push(`src/${relative(source, file).replaceAll("\\", "/")}:${index + 1}: a time limit as a bare number; take it from testTime (testLimit(...)), or end the line with \`// ${notALimit}\` when the time is what the test checks: ${code.slice(0, 200)}`);
    });
  }
  assert.deepEqual(problems, [], problems.join("\n"));
});

test("the guard finds the bare limits and leaves the times that are data", () => {
  for (const bare of [
    `test("opens", { skip: !edge, timeout: 90_000 }, async () => {`,
    `test("opens", { timeout: 5000 }, async () => {`,
    `spawnSync(process.execPath, [file], { encoding: "utf8", timeout: 15_000 });`,
    "const end = Date.now() + 7000;",
    "await evaluate(`new Promise(resolve => { const end = Date.now() + 15000; (function check() {`);",
    "const deadline = performance.now() + 5000;",
    "const timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);",
    "const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timeout`)); }, 10_000);",
    "export async function waitForBrowserPort(profile: string, browser: ChildProcess, timeoutMs = 5000): Promise<string> {",
    "const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(",
    "const waitFor = async (expression: string, what: string, timeout = 8000) => {",
    "const pages = await (await fetch(url, { signal: AbortSignal.timeout(10_000) })).json();",
  ]) assert.ok(bareLimit(bare), bare);

  for (const data of [
    `test("opens", { skip: !edge, timeout: testLimit(90_000) }, async () => {`,
    "const end = Date.now() + testLimit(7000);",
    "await evaluate(`new Promise(resolve => { const end = Date.now() + ${testLimit(15000)}; (function check() {`);",
    "const deadline = Date.now() + timeout;",
    "const timer = setTimeout(() => reject(Error(testLimitRanOut(testLimit(12_000), `the answer to ${method}`))), testLimit(12_000));",
    "nextRunAt: new Date(Date.now() + 3600_000).toISOString(),",
    `toaster.show({ message: "running", timeout: 0 }, "job");`,
    "await new Promise(resolve => setTimeout(resolve, 300));",
    "const settle = (milliseconds = 300) => new Promise(resolve => setTimeout(resolve, milliseconds)); // not a limit",
    "await assert.rejects(waitForBrowserPort(root, new ChildProcess(), 100), /timed out after 100 ms/);",
    "export async function endBrowser(browser: ChildProcess | undefined, timeoutMs = testLimit(5000)): Promise<void> {",
    "const pages = await (await fetch(url, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();",
  ]) assert.ok(!bareLimit(data), data);
});

test("the frontend and the .NET tests take their limits with the same factor on CI", () => {
  const dotnet = readFileSync(fileURLToPath(new URL("../../../TestSupport/TestTime.cs", import.meta.url)), "utf8");
  const factor = /CiScale = (\d+)/.exec(dotnet);
  assert.ok(factor, "TestTime.cs declares CiScale");
  assert.equal(testTimeCiScale, Number(factor[1]));
});

test("the factor is the one given, else that of CI or of a developer machine", () => {
  assert.equal(readScale(undefined, false), 1);
  assert.equal(readScale(undefined, true), testTimeCiScale);
  assert.equal(readScale("", true), testTimeCiScale);
  assert.equal(readScale("  ", false), 1);
  assert.equal(readScale("2.5", false), 2.5);
  assert.equal(readScale("3", true), 3);
  for (const wrong of ["0", "-2", "abc", "Infinity"]) {
    assert.equal(readScale(wrong, false), 1, wrong);
    assert.equal(readScale(wrong, true), testTimeCiScale, wrong);
  }
});

test("a limit is scaled by the factor of the run, and a time that is no limit stays", () => {
  assert.equal(testLimit(1000), Math.round(1000 * testTimeScale));
  assert.equal(testLimit(7), Math.round(7 * testTimeScale));
  assert.equal(testLimit(0), 0);
  assert.equal(testLimit(-5), -5);
  assert.equal(testLimit(Infinity), Infinity);
  assert.ok(Number.isNaN(testLimit(NaN)));
});

test("a limit that ran out says how long it was and for what", () => {
  const message = testLimitRanOut(12_000, "the answer to Runtime.evaluate");
  assert.match(message, /^Time limit ran out: 12 s/);
  assert.match(message, /without the answer to Runtime\.evaluate\.$/);
  assert.match(testLimitRanOut(250, "the frame"), /^Time limit ran out: 250 ms/);
  if (testTimeScale !== 1) assert.match(message, new RegExp(`\\(${testTimeScale} times the limit of a developer machine`));
});
