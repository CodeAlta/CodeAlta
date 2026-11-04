import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { AutomationTriggerItem } from "#neoastra";
import { locales, translate } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { TriggerRow } from "./AutomationEditor";
import { newTrigger } from "./automations";
import type { AutomationsHub } from "./automationsHub";

const never = () => assert.fail("rendering must not act");
const hub = { preview: never } as unknown as AutomationsHub;

function row(trigger: AutomationTriggerItem, change: { chat?: boolean; locale?: typeof locales[number] } = {}) {
  const locale = change.locale ?? "en";
  return renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale, choice: locale, setLanguage: never } },
    createElement(TriggerRow, { hub, trigger, chat: change.chat ?? false, disabled: false, onChange: never, onRemove: never })));
}

test("a command trigger is written as a command and the folder it runs in", () => {
  const html = row({ ...newTrigger("command"), command: "gh run watch 123 --exit-status", folder: "tools" });
  assert.match(html, /class="[^"]*automation-command"[\s\S]*?aria-label="Command"[\s\S]*?value="gh run watch 123 --exit-status"/);
  assert.match(html, /class="[^"]*automation-command-folder"[\s\S]*?value="tools"/);
  assert.match(html, /aria-label="Working folder"/);
  assert.match(html, /<option value="command" selected="">Command<\/option>/);
  // It is neither a time on the clock nor an event someone writes: no next times, no authors.
  assert.doesNotMatch(html, /automation-trigger-preview|aria-label="Authors"|aria-label="Event"/);
});

test("the folder of a command reads as the one it runs in when it is left empty", () => {
  assert.match(row(newTrigger("command")), /placeholder="Project folder"/);
  assert.match(row(newTrigger("command"), { chat: true }), /placeholder="Home folder"/);
  for (const locale of locales) {
    const html = row(newTrigger("command"), { chat: true, locale });
    assert.ok(html.includes(`placeholder="${translate(locale, "Home folder")}"`) && html.includes(`aria-label="${translate(locale, "Command")}"`), locale);
  }
});

const context = { projectId: null, runsOn: null, prompt: "Triage it.", savedId: null, saved: false, index: 0 };
const integrated = (trigger: AutomationTriggerItem, locale: typeof locales[number] = "en") => renderToStaticMarkup(createElement(ShellLanguageContext.Provider,
  { value: { locale, choice: locale, setLanguage: never } }, createElement(TriggerRow, { hub, trigger, chat: true, disabled: false, onChange: never, onRemove: never, context })));

test("a trigger of another tool shows its settings, its secrets by name, and how to set it up", () => {
  const jira = newTrigger("jira_query");
  const html = integrated({ ...jira, integration: { ...jira.integration!, site: "https://acme.atlassian.net", token: "${secret:JIRA_TOKEN}", query: "project = PLAT" } });
  // The token field shows the name of its secret, never a value: the trigger keeps the reference.
  assert.match(html, /aria-label="API token"[^>]*value="JIRA_TOKEN"/);
  assert.match(html, /aria-label="JQL query"[^>]*value="project = PLAT"/);
  assert.match(html, /Write the answer back where the event came from/);
  assert.match(html, /Test the connection[\s\S]*Show a sample event[\s\S]*Try it on this event/);
  assert.doesNotMatch(html, /automation-trigger-preview|aria-label="Authors"/, "no next times, no authors");

  const slack = integrated(newTrigger("slack"));
  assert.match(slack, /aria-label="Channel id"[\s\S]*aria-label="App-level token"[\s\S]*aria-label="Bot token"/);
  assert.match(slack, /Socket Mode/);
  const webhook = integrated(newTrigger("webhook"));
  assert.match(webhook, /aria-label="Hook"[\s\S]*Make a hook[\s\S]*aria-label="Signing secret"/);
  assert.match(webhook, /aria-label="Fields that reach the prompt"/);
  assert.doesNotMatch(webhook, /Write the answer back/, "a webhook has nowhere to reply");
  assert.match(integrated(newTrigger("incident_io")), /aria-label="API key"/);
  for (const locale of locales) assert.ok(integrated(newTrigger("slack"), locale).includes(translate(locale, "Test the connection")), locale);
});

test("the other triggers keep their own fields", () => {
  assert.match(row(newTrigger("issue")), /aria-label="Authors"/);
  assert.match(row(newTrigger("pull_request")), /aria-label="Event"[\s\S]*aria-label="Authors"/);
  assert.doesNotMatch(row(newTrigger("jira")), /aria-label="Authors"|automation-command/);
  assert.doesNotMatch(row(newTrigger("daily")), /aria-label="Authors"|automation-command/);
});
