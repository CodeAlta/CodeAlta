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
  assert.doesNotMatch(slack, /replies in the threads it answered/, "a trigger on mentions answers each mention");
  assert.match(integrated({ ...newTrigger("slack"), event: "message" }), /Also start on people&#x27;s replies in the threads it answered/);
  const webhook = integrated(newTrigger("webhook"));
  assert.match(webhook, /aria-label="Hook"[\s\S]*Make a hook[\s\S]*aria-label="Signing secret"/);
  assert.match(webhook, /aria-label="Fields that reach the prompt"/);
  assert.doesNotMatch(webhook, /Write the answer back/, "a webhook has nowhere to reply");
  assert.match(integrated(newTrigger("incident_io")), /aria-label="API key"/);
  // Once it names a hook, its address is hidden until asked for, and never written in the page before.
  const hooked = newTrigger("incident_io");
  const masked = integrated({ ...hooked, integration: { ...hooked.integration!, hook: "abcdefghijklmnopqrstuvwxyz" } });
  assert.match(masked, /Address of the hook[\s\S]*Show the address/);
  assert.doesNotMatch(masked, /api\/v1\/hooks/);
  for (const locale of locales) assert.ok(integrated(newTrigger("slack"), locale).includes(translate(locale, "Test the connection")), locale);
});

test("the other triggers keep their own fields", () => {
  assert.match(row(newTrigger("issue")), /aria-label="Authors"/);
  assert.match(row(newTrigger("pull_request")), /aria-label="Event"[\s\S]*aria-label="Authors"/);
  assert.doesNotMatch(row(newTrigger("jira")), /aria-label="Authors"|automation-command/);
  assert.doesNotMatch(row(newTrigger("daily")), /aria-label="Authors"|automation-command/);
});

test("a trigger of a source shows its events and the fields its source says, and a secret by its name", () => {
  const source = {
    type: "acme/deploy", displayName: "Deployment", description: null, needsProject: false, pluginId: "acme", script: null,
    events: [{ id: "succeeded", label: "Succeeded", description: null }, { id: "failed", label: "Failed", description: null }],
    fields: [
      { id: "environment", label: "Environment", kind: "text", description: null, required: true, choices: [], placeholder: "prod" },
      { id: "token", label: "Token", kind: "secret", description: null, required: false, choices: [], placeholder: null },
      { id: "stage", label: "Stage", kind: "choice", description: null, required: false, choices: [{ id: "beta", label: "Beta" }], placeholder: null },
      { id: "strict", label: "Strict", kind: "flag", description: null, required: false, choices: [], placeholder: null },
    ],
  };
  const trigger: AutomationTriggerItem = { ...newTrigger("hourly"), type: "acme/deploy", event: "", source: { on: ["failed"], settingsJson: JSON.stringify({ environment: "prod", token: "${secret:DEPLOY}" }) } };
  const html = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(TriggerRow, { hub, trigger, chat: true, disabled: false, onChange: never, onRemove: never, sources: [source] })));
  assert.match(html, /<option value="acme\/deploy" selected="">Deployment<\/option>/);
  assert.match(html, /role="group" aria-label="Events"/);
  assert.match(html, /type="checkbox" checked=""\/><span class="bp6-control-indicator"><\/span>Failed/, "the event it chose is checked");
  assert.match(html, /type="checkbox"\/><span class="bp6-control-indicator"><\/span>Succeeded/, "and only it");
  assert.match(html, /aria-label="Environment"[^>]*value="prod"|value="prod"[^>]*aria-label="Environment"/);
  assert.match(html, /value="DEPLOY"/, "a secret shows by its name, never as a value");
  assert.match(html, /aria-label="Stage"/);
  assert.match(html, /Strict/);
  // A trigger whose plugin is not running stays as it is written.
  const missing = renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } },
    createElement(TriggerRow, { hub, trigger, chat: true, disabled: false, onChange: never, onRemove: never, sources: [] })));
  assert.match(missing, /<option value="acme\/deploy" selected="">acme\/deploy<\/option>/);
});
