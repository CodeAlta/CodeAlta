import { existsSync } from "node:fs";

/** The browser the `*.browser.test.ts` files drive, or undefined when there is none and they skip. */
export const browserExecutable: string | undefined =
  ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

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
