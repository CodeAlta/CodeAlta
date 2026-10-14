import assert from "node:assert/strict";
import test from "node:test";
import { claudeLoginCommand, cliInstallFailure, cliInstalling, cliInstallView, quotePath, sameConfigDir, validConfigDir } from "./claudeCli";

const windows = { windows: true, home: "C:\\Users\\me" };
const posix = { windows: false, home: "/Users/me" };

test("the login command of PowerShell sets the folder of the account, then runs the executable the provider runs", () => {
  const pinned = "C:\\Users\\me\\.alta\\cache\\claude-code\\2.1.292\\win-x64\\claude.exe";
  assert.equal(claudeLoginCommand(windows, pinned, "~/.claude-business"),
    "$env:CLAUDE_CONFIG_DIR=\"$HOME\\.claude-business\"; & \"$HOME\\.alta\\cache\\claude-code\\2.1.292\\win-x64\\claude.exe\"");
  assert.equal(claudeLoginCommand(windows, pinned, " "), "& \"$HOME\\.alta\\cache\\claude-code\\2.1.292\\win-x64\\claude.exe\"", "without a folder of its own");
  assert.equal(claudeLoginCommand(windows, null, ""), "claude", "without an executable, the one on PATH");
  assert.equal(claudeLoginCommand(windows, "D:\\tools\\claude.exe", "c:\\users\\ME\\work"), "$env:CLAUDE_CONFIG_DIR=\"$HOME\\work\"; & \"D:\\tools\\claude.exe\"",
    "the home folder of Windows is found whatever its case");
});

test("the login command of a POSIX shell gives the folder to the executable alone", () => {
  assert.equal(claudeLoginCommand(posix, "/Users/me/.alta/cache/claude-code/2.1.292/osx-arm64/claude", "~/.claude-business"),
    "CLAUDE_CONFIG_DIR=\"$HOME/.claude-business\" \"$HOME/.alta/cache/claude-code/2.1.292/osx-arm64/claude\"");
  assert.equal(claudeLoginCommand(posix, "/opt/claude", ""), "\"/opt/claude\"");
  assert.equal(claudeLoginCommand(posix, null, "/srv/claude"), "CLAUDE_CONFIG_DIR=\"/srv/claude\" claude");
  assert.equal(quotePath("/Users/meagain/x", posix), "\"/Users/meagain/x\"", "a folder that only starts like home is not in it");
});

test("a path is escaped so that the shell expands nothing but $HOME", () => {
  assert.equal(quotePath("~/a $b \"c\" `d`", windows), "\"$HOME\\a `$b `\"c`\" ``d``\"");
  assert.equal(quotePath("/tmp/a $b \"c\" `d` \\e", posix), "\"/tmp/a \\$b \\\"c\\\" \\`d\\` \\\\e\"");
  assert.equal(quotePath("~", posix), "\"$HOME\"");
});

test("a folder is a full path or starts with ~, and two spellings of one folder are the same setting", () => {
  for (const folder of ["", " ", "~", "~/x", "/x", "C:\\x", "c:/x", "\\\\server\\share"]) assert.equal(validConfigDir(folder), true, folder);
  for (const folder of ["x", ".claude", "~x", "C:x"]) assert.equal(validConfigDir(folder), false, folder);
  assert.equal(sameConfigDir("~/.claude-work/", " ~/.claude-work"), true);
  assert.equal(sameConfigDir(null, ""), true);
  assert.equal(sameConfigDir("~/.claude", "~/.claude-work"), false);
});

test("the download is read from the host and its failure is said", () => {
  const view = cliInstallView({ state: "downloading", receivedBytes: "1024", totalBytes: "4096", error: null });
  assert.deepEqual(view, { state: "downloading", received: 1024, total: 4096, error: null });
  assert.equal(cliInstalling(view), true);
  assert.equal(cliInstallView({ state: "unknown", receivedBytes: "x", totalBytes: "", error: null }).state, "idle");
  assert.equal(cliInstalling(cliInstallView({ state: "done", receivedBytes: "1", totalBytes: "1", error: null })), false);
  assert.match(cliInstallFailure("checksum"), /does not match/);
  assert.match(cliInstallFailure("busy_elsewhere"), /Another CodeAlta/);
  assert.equal(cliInstallFailure("whatever"), "Claude Code could not be installed.");
});
