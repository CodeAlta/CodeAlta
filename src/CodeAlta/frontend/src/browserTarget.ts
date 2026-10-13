import { existsSync } from "node:fs";
import { spawn, type ChildProcess } from "node:child_process";
import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import type { Readable } from "node:stream";

/** The variable that names the browser to drive, in the place of the Edge the system has. */
export const browserVariable = "CODEALTA_TEST_BROWSER";

/**
 * Where Edge is looked for, in order. A path the variable gives is taken as it is: a browser that was asked for
 * and is not there fails the tests at its start, it does not skip them.
 */
export function findBrowser(env: NodeJS.ProcessEnv, home: string, exists: (path: string) => boolean): string | undefined {
  const named = env[browserVariable]?.trim();
  if (named) return named;
  return [
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    join(home, "Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"),
    "/usr/bin/microsoft-edge",
    "/usr/bin/microsoft-edge-stable",
    "/opt/microsoft/msedge/msedge",
  ].find(exists);
}

/** The browser the `*.browser.test.ts` files drive, or undefined when there is none and they skip. */
export const browserExecutable: string | undefined = findBrowser(process.env, homedir(), existsSync);

/**
 * Ends the browser a test started, and returns once it is gone. Edge's launcher can exit while the browser it
 * started goes on, so a test asks the browser itself to close (`Browser.close`) before it calls this; what is
 * left is ended here: the whole process tree on Windows, where a kill of the launcher leaves its children, and
 * the process elsewhere.
 */
export async function endBrowser(browser: ChildProcess | undefined, timeoutMs = 5000): Promise<void> {
  if (!browser?.pid) return;
  if (process.platform === "win32") {
    const killer = spawn("taskkill", ["/F", "/T", "/PID", String(browser.pid)], { stdio: "ignore", windowsHide: true });
    await new Promise(resolve => { killer.once("exit", resolve); killer.once("error", resolve); });
    return;
  }
  if (browser.exitCode !== null || browser.signalCode !== null) return;
  let timer: NodeJS.Timeout | undefined;
  const gone = new Promise<void>(resolve => browser.once("exit", () => resolve()));
  browser.kill("SIGKILL");
  await Promise.race([gone, new Promise(resolve => { timer = setTimeout(resolve, timeoutMs); })]);
  clearTimeout(timer);
}

/**
 * What every one of those files starts the browser with, before its own options, its `--user-data-dir` and its
 * `--remote-debugging-port`.
 *
 * `--guest` is there for more than tidiness. A profile that has never signed in makes Edge ask the operating
 * system for an identity when it starts, and on Windows that is an interactive logon for the account the tests
 * run as. Where that account is a local one it has no password Edge can present, so the attempt fails and
 * counts as a wrong password (event 4625, `msedge.exe` as the caller). Each of these files starts a browser of
 * its own and the runner starts them together, so one `npm test` spent a dozen failed logons in seconds, which
 * is enough to trip a ten-attempt account lockout policy and lock the user out of their own machine. Guest mode
 * carries no profile identity, so nothing is asked of the operating system.
 */
export const browserBaseArgs: readonly string[] = Object.freeze([
  "--headless=new",
  "--disable-gpu",
  "--no-first-run",
  "--disable-background-networking",
  "--disable-extensions",
  "--edge-skip-compat-layer-relaunch",
  "--guest",
]);

/** Waits through missing/partial port files, with a real deadline and bounded launch diagnostics. */
export async function waitForBrowserPort(profile: string, browser: ChildProcess, timeoutMs = 5000): Promise<string> {
  let stderr = "", probe = "not read", launchError: Error | undefined;
  let exitCode = browser.exitCode, signal = browser.signalCode;
  const capture = (chunk: Buffer | string) => { stderr = (stderr + chunk.toString()).slice(-4096); };
  const failed = (error: Error) => { launchError = error; };
  const exited = (code: number | null, endedBy: NodeJS.Signals | null) => { exitCode = code; signal = endedBy; };
  browser.on("error", failed);
  browser.on("exit", exited);
  browser.stderr?.on("data", capture);
  const failure = (reason: string) => new Error(`Browser startup ${reason}; pid=${browser.pid ?? "none"}; exit=${exitCode ?? "none"}; signal=${signal ?? "none"}; profile=${profile}; ${probe}\n${stderr}`, { cause: launchError });
  try {
    const deadline = performance.now() + timeoutMs;
    while (performance.now() < deadline) {
      if (launchError) throw failure(`failed: ${launchError.message}`);
      // Edge's launcher may exit successfully while the browser it started is still coming up.
      if (exitCode !== null && exitCode !== 0 || signal !== null) throw failure("exited before readiness");
      try {
        const lines = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/);
        const port = lines[0];
        if (/^\d+$/.test(port) && Number(port) > 0 && Number(port) <= 65535 && lines[1]?.startsWith("/devtools/browser/")) return port;
        probe = "DevToolsActivePort is empty or incomplete";
      } catch (error) { probe = `DevToolsActivePort read: ${(error as NodeJS.ErrnoException).code ?? "failed"}`; }
      // Delay on empty/partial files too: an attempt counter otherwise expires in milliseconds.
      await new Promise(resolve => setTimeout(resolve, Math.min(50, Math.max(0, deadline - performance.now()))));
    }
    throw failure(`timed out after ${timeoutMs} ms`);
  } finally {
    browser.off("error", failed);
    browser.off("exit", exited);
    browser.stderr?.off("data", capture);
    browser.stderr?.resume(); // Keep the pipe drained after readiness without retaining its contents.
    // What the browser leaves behind holds the other end of the pipe (Edge's crash handler, on macOS for minutes
    // after the browser is gone): the pipe alone does not keep the process of the test alive.
    (browser.stderr as (Readable & { unref?: () => void }) | null)?.unref?.();
  }
}
