import assert from "node:assert/strict";
import test from "node:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { ReactElement } from "react";
import type { PluginUiEvent } from "#neoastra";
import { noticeToast } from "./PluginUiHost";

const notice = (change: Partial<PluginUiEvent>): PluginUiEvent => ({ kind: "notify", requestId: null, dialog: null, pluginKey: null, title: null, message: null, text: null,
  html: null, script: null, scriptProblem: null, items: null, buttons: null, actions: false, mode: null, sessionId: null, projectId: null, tone: null, waits: false, ...change });

test("a notification shows its title above its text, in its tone, and its action opens its session", () => {
  const opened: string[] = [];
  const toast = noticeToast(notice({ title: "Nightly review", message: "Completed in 3 min 12 s.\nTwo pull requests need a review.", tone: "success", sessionId: "s1" }),
    "Open", id => opened.push(id));
  assert.equal(toast.intent, "success");
  assert.equal(toast.icon, "tick-circle");
  assert.equal(toast.timeout, 8000);
  const markup = renderToStaticMarkup(toast.message as ReactElement);
  assert.match(markup, /^<strong>Nightly review<\/strong><div class="plugin-notice-body">Completed in 3 min 12 s.\nTwo pull requests need a review.<\/div>$/);
  assert.equal(toast.action?.text, "Open");
  toast.action?.onClick?.({} as never);
  assert.deepEqual(opened, ["s1"]);
});

test("a message of a plugin stays as it was, and a notification that came in the background waits", () => {
  const plain = noticeToast(notice({ message: "Plain message." }), "Open", () => assert.fail("nothing to open"));
  assert.deepEqual([plain.message, plain.intent, plain.icon, plain.action], ["Plain message.", "none", "info-sign", undefined]);
  assert.deepEqual([noticeToast(notice({ message: "Careful.", tone: "warning" }), "Open", () => {}).intent, noticeToast(notice({ title: "Failed", tone: "danger" }), "Open", () => {}).icon],
    ["warning", "error"]);
  assert.equal(noticeToast(notice({ title: "Nightly review", waits: true }), "Open", () => {}).timeout, 0);
});
