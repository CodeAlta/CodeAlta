import assert from "node:assert/strict";
import { ChildProcess, spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { browserVariable, endBrowser, findBrowser, waitForBrowserPort } from "./browserTarget";

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

test("the stderr pipe of a ready browser does not keep the test process running", async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-browser-startup-"));
  // What a browser leaves behind (its crash handler) holds the pipe as this child does.
  const browser = spawn(process.execPath, ["-e", "setTimeout(() => {}, 60000)"], { stdio: ["ignore", "ignore", "pipe"] });
  const pipes = () => process.getActiveResourcesInfo().filter(name => name === "PipeWrap").length;
  try {
    const held = pipes();
    await writeFile(join(root, "DevToolsActivePort"), "9222\n/devtools/browser/id\n");
    assert.equal(await waitForBrowserPort(root, browser), "9222");
    assert.equal(pipes(), held - 1, "the pipe is read, and is not what the process waits for");
    assert.equal(browser.stderr!.readableFlowing, true);
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

test("the browser is the one the variable names, else the first Edge of the system", () => {
  const mac = "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
  const own = join("/Users/me", "Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
  assert.equal(findBrowser({}, "/Users/me", path => path === mac || path === own), mac);
  assert.equal(findBrowser({}, "/Users/me", path => path === own), own);
  assert.equal(findBrowser({}, "/home/me", path => path === "/usr/bin/microsoft-edge-stable" || path === "/opt/microsoft/msedge/msedge"), "/usr/bin/microsoft-edge-stable");
  assert.equal(findBrowser({}, "C:/Users/me", path => path.endsWith("/Program Files/Microsoft/Edge/Application/msedge.exe")), "C:/Program Files/Microsoft/Edge/Application/msedge.exe");
  assert.equal(findBrowser({}, "/home/me", () => false), undefined);
  assert.equal(findBrowser({ [browserVariable]: "  " }, "/home/me", () => false), undefined);
  // A browser that was asked for is not looked for: one that is missing fails at its start, it is not skipped.
  assert.equal(findBrowser({ [browserVariable]: " /opt/chromium/chrome " }, "/Users/me", path => path === mac), "/opt/chromium/chrome");
});

test("an owned browser is ended before its profile is removed", async () => {
  const browser = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
  const gone = new Promise<void>(resolve => browser.once("exit", () => resolve()));
  await new Promise(resolve => browser.once("spawn", resolve));
  await endBrowser(browser);
  let timer: NodeJS.Timeout | undefined;
  await Promise.race([gone, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("the process is still there")), 5000); })]);
  clearTimeout(timer);
  assert.ok(browser.exitCode !== null || browser.signalCode !== null);
  await endBrowser(browser); // One that is gone, or was never started, is nothing to end.
  await endBrowser(undefined);
});
