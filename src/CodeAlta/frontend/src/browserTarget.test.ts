import assert from "node:assert/strict";
import { ChildProcess, spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { waitForBrowserPort } from "./browserTarget";

test("an existing empty DevTools file is still a pending browser launch", async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-startup-"));
  const file = join(root, "DevToolsActivePort");
  let writing: Promise<void> | undefined;
  try {
    await writeFile(file, "");
    writing = new Promise(resolve => setTimeout(resolve, 250)).then(() => writeFile(file, "9222\n/devtools/browser/synthetic\n"));
    assert.equal(await waitForBrowserPort(root, new ChildProcess()), "9222");
  } finally {
    await writing;
    await rm(root, { recursive: true, force: true });
  }
});

test("a successful launcher exit still waits for the full port record", async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-startup-"));
  const browser = new ChildProcess();
  let writing: Promise<void> | undefined;
  try {
    await writeFile(join(root, "DevToolsActivePort"), "92");
    const ready = waitForBrowserPort(root, browser);
    browser.emit("exit", 0, null);
    writing = new Promise(resolve => setTimeout(resolve, 100)).then(() => writeFile(join(root, "DevToolsActivePort"), "9222\n/devtools/browser/synthetic"));
    assert.equal(await ready, "9222");
    assert.equal(browser.listenerCount("exit"), 0);
    assert.equal(browser.listenerCount("error"), 0);
  } finally { await writing; await rm(root, { recursive: true, force: true }); }
});

test("startup failure includes the child exit and bounded stderr", async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-startup-"));
  const browser = spawn(process.execPath, ["-e", "process.stderr.write('x'.repeat(10000) + ' launch failed', () => process.exit(17))"], { stdio: ["ignore", "ignore", "pipe"] });
  try {
    await assert.rejects(waitForBrowserPort(root, browser), (error: Error) => {
      assert.match(error.message, /exit=17/);
      assert.match(error.message, /launch failed/);
      assert.ok(error.message.length < 4600);
      return true;
    });
  } finally { browser.kill(); await rm(root, { recursive: true, force: true }); }
});

test("spawn errors and missing port deadlines are reported, not retried", async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-startup-"));
  try {
    const browser = new ChildProcess();
    const failed = waitForBrowserPort(root, browser);
    browser.emit("error", new Error("synthetic spawn failure"));
    await assert.rejects(failed, /synthetic spawn failure/);
    await assert.rejects(waitForBrowserPort(root, new ChildProcess(), 100), /timed out after 100 ms.*ENOENT/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
