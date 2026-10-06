import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { TerminalItem } from "#neoastra";
import { locales, translate, type Locale } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { TerminalList } from "./TerminalList";
import { defaultTerminalLook, maximumTerminalFontSize, minimumTerminalFontSize } from "./terminalLook";
import { TerminalOptions } from "./TerminalOptions";

const never = () => assert.fail("rendering must not act");
const render = (locale: Locale, element: ReactElement) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale, choice: locale, setLanguage: never } }, element));
const terminal = (id: string, more: Partial<TerminalItem> = {}): TerminalItem => ({
  id, projectId: "p", sessionId: null, title: "C:\\code\\app", titled: false, folder: "C:\\code\\app", profile: "pwsh", profileName: "PowerShell", programTitle: null,
  running: true, exitCode: null, integrated: true, busy: false, command: null, lastExitCode: null, columns: 120, rows: 30, created: "2026-10-06T00:00:00Z",
  open: false, attention: false, agent: false, ...more });

test("the terminals of a project are rows under a title: each opens its tab, and can be renamed and ended", () => {
  for (const locale of locales) {
    const html = render(locale, createElement(TerminalList, { active: "b", onOpen: never, onClose: never, rename: () => Promise.resolve("ok"), terminals: [
      terminal("a"), terminal("b", { title: "<dev> server", titled: true, busy: true, command: "npm run dev" }),
      terminal("c", { running: false, exitCode: 3 }), terminal("d", { attention: true })] }));
    const rows = html.split('<div class="session-row terminal-row').slice(1);
    assert.equal(rows.length, 4);
    assert.ok(html.includes(`aria-label="${translate(locale, "Terminals")}"`) && html.includes(`<span>${translate(locale, "Terminals")}</span>`), html);
    // A terminal without a title is named by its folder, which is cut at its start when it is too long; its shell is said beside it.
    assert.ok(rows[0].includes('data-folder="true"><bdi>C:\\code\\app</bdi>') && rows[0].includes("<span>PowerShell</span>"), rows[0]);
    assert.ok(rows[0].includes('aria-pressed="false"') && rows[0].includes('data-ended="false"'));
    assert.ok(rows[0].includes(`aria-label="${translate(locale, "Rename {title}", { title: "C:\\code\\app" })}"`), rows[0]);
    assert.ok(rows[0].includes(`title="${translate(locale, "End terminal")}"`), rows[0]);
    // The one whose tab is in front, with a title it was given and a command that runs.
    assert.ok(rows[1].includes('aria-pressed="true"') && rows[1].includes('data-folder="false"><bdi>&lt;dev&gt; server</bdi>'), rows[1]);
    assert.ok(rows[1].includes("npm run dev") && !rows[1].includes("<span>PowerShell</span>"), rows[1]);
    // One whose program has ended shows how, and is closed rather than ended.
    assert.ok(rows[2].startsWith('" data-ended="true"') && rows[2].includes("<span>3</span>"), rows[2]);
    assert.ok(rows[2].includes(`title="${translate(locale, "Close terminal")}"`) && rows[2].includes(translate(locale, "Ended (exit code {code})", { code: 3 })), rows[2]);
    assert.ok(rows[3].includes('class="terminal-attention"') && !rows[0].includes("terminal-attention"), rows[3]);
    // A terminal a session created says so.
    assert.ok(!html.includes("terminal-agent"));
    assert.ok(render(locale, createElement(TerminalList, { active: null, onOpen: never, onClose: never, rename: () => Promise.resolve("ok"), terminals: [terminal("e", { agent: true })] }))
      .includes(`class="terminal-agent" role="img" aria-label="${translate(locale, "Created by a session")}"`));
  }
  // A project without a terminal has no such list.
  assert.equal(render("en", createElement(TerminalList, { active: null, onOpen: never, onClose: never, rename: () => Promise.resolve("ok"), terminals: [] })), "");
});

test("the options of the terminals have a control for each preference, which shows what is chosen", () => {
  for (const locale of locales) {
    const shells = [{ id: "pwsh", name: "PowerShell" }, { id: "cmd", name: "Command <Prompt>" }, { id: "wsl:Ubuntu", name: "Ubuntu (WSL)" }];
    const html = render(locale, createElement(TerminalOptions, { look: { ...defaultTerminalLook, cursorStyle: "block", fontSize: 15, copyOnSelect: true, scrollback: 100_000, shellIntegration: false, shell: "wsl:Ubuntu" }, onLook: never, shells }));
    for (const label of ["Cursor", "Blink cursor", "Text size", "Copy on select", "Scrollback", "Shell", "Shell integration", "Bar", "Block", "Underline"] as const) {
      assert.ok(html.includes(`>${translate(locale, label)}<`), `${locale}: ${label}`);
    }
    // The cursor is one of three, the size a number with its limits, the scrollback a choice, the others are on or off.
    assert.equal(/aria-checked="true"[^>]*><span class="bp6-button-text">([^<]*)</.exec(html)?.[1], translate(locale, "Block"));
    assert.equal(html.split('role="radio"').length - 1, 3);
    assert.ok(html.includes(`aria-valuemin="${minimumTerminalFontSize}"`) && html.includes(`aria-valuemax="${maximumTerminalFontSize}"`) && html.includes('value="15"'), html);
    assert.equal(html.split('type="checkbox"').length - 1, 3);
    assert.equal(html.split('type="checkbox" checked=""').length - 1, 2, "The cursor blinks and what is selected is copied; the shells start as they are.");
    assert.match(html, /<option value="100000"[^>]* selected=""/);
    // The shell is one of those of the system: the one that was chosen, or the default one when it is gone or there is no choice.
    assert.match(html, /<option value="wsl:Ubuntu"[^>]* selected=""/);
    assert.ok(html.includes("Command &lt;Prompt&gt;"));
    assert.equal(html.split("<option").length - 1, 6);
    const gone = render(locale, createElement(TerminalOptions, { look: { ...defaultTerminalLook, shell: "fish" }, onLook: never, shells }));
    assert.match(gone, /<option value="pwsh"[^>]* selected=""/);
    for (const few of [[], shells.slice(0, 1)]) {
      assert.equal(render(locale, createElement(TerminalOptions, { look: defaultTerminalLook, onLook: never, shells: few })).split("<select").length - 1, 1, "One shell is no choice.");
    }
    // Every control is named by the text beside it.
    for (const id of html.matchAll(/<label for="([^"]+)"/g)) assert.ok(html.includes(` id="${id[1]}"`), id[1]);
  }
});
