import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { PullRequestPromptItem } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { PullRequestButton } from "./PullRequestButton";
import { pullRequestForm, PullRequestSettings, type PullRequestSettingsApi } from "./PullRequestSettings";

const never = () => assert.fail("rendering must not ask the host");
const render = (element: ReactElement) => renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const item = (more: Partial<PullRequestPromptItem> = {}): PullRequestPromptItem =>
  ({ id: "default", name: "Default", description: "Opens it.", source: "builtin", overridden: false, content: "Create a pull request.", file: null, ...more });

test("the button is one quiet control of the strip, and asks nothing until its menu opens", () => {
  const html = render(createElement(PullRequestButton, { api: { list: never } as never, epoch: "e", projectId: "p", sessionId: "s", onNotice: never }));
  assert.match(html, /<button type="button" class="project-context-pull" title="Create a pull request" aria-label="Create a pull request" aria-haspopup="menu" aria-expanded="false">/);
  assert.ok(!html.includes("Default"), "the kinds are read when the menu opens");
});

test("a kind of the user or of a project is edited as it is; a new one starts from the text it is made from", () => {
  const mine = item({ source: "project", name: "Team", description: null, content: "The way of the team.", file: "C:\\app\\.alta\\prompts\\pull-requests\\default.pr.md" });
  assert.deepEqual(pullRequestForm(mine, null, "global"), { original: mine, id: "default", scope: "project", name: "Team", description: "", content: "The way of the team." });
  assert.deepEqual(pullRequestForm(null, item(), "project"), { original: null, id: "default", scope: "project", name: "Default", description: "Opens it.", content: "Create a pull request." });
  assert.deepEqual(pullRequestForm(null, null, "global"), { original: null, id: "", scope: "global", name: "", description: "", content: "" });
  // What ships is not a file to edit: its form is the one of a copy.
  assert.equal(pullRequestForm(item(), null, "global").original, null);
});

test("the settings page says what it is for, and that a window without a host has none", () => {
  const api = { list: () => new Promise(() => { }), save: never, delete: never } as unknown as PullRequestSettingsApi;
  const html = render(createElement(PullRequestSettings, { api, epoch: "e", project: { id: "p", name: "Alpha" } }));
  assert.match(html, /<h1>Pull requests<\/h1><p>What a session is told when you ask it to create a pull request\.<\/p>/);
  assert.ok(html.includes("New kind"));
});
