import assert from "node:assert/strict";
import test from "node:test";
import { translate } from "./localization";
import { providerTourSteps, startsProviderTour } from "./providerTour";

const provider = (key: string, type: string, variable: string | null = null, enabled = false) =>
  ({ key, type, effectiveName: key[0].toUpperCase() + key.slice(1), enabled, apiKeyEnv: null, defaults: { apiKeyEnv: variable } });
const defaults = [provider("alibaba", "openai-chat", "CODEALTA_ALIBABA_API_KEY"), provider("anthropic", "anthropic", "CODEALTA_ANTHROPIC_API_KEY"),
  provider("codex", "codex"), provider("copilot", "copilot"), provider("openai", "openai-responses", "CODEALTA_OPENAI_API_KEY")];

test("the tour goes from the ChatGPT subscription to its sign-in, then to the providers with a key", () => {
  const steps = providerTourSteps(defaults);
  assert.deepEqual(steps.map(step => [step.id, step.target, step.provider]),
    [["subscription", "provider", "codex"], ["sign-in", "account", "codex"], ["others", "list", null]]);
  // Both subscription sign-ins are named; the tour continues with Codex.
  assert.equal(translate("en", steps[0].body, steps[0].parameters),
    "No model provider is enabled yet. Codex (ChatGPT) and Copilot (GitHub Copilot) sign in with a subscription you already have: there is no API key to create. This guide continues with Codex.");
  assert.equal(translate("en", steps[1].body, steps[1].parameters).includes("Codex is enabled"), true);
  // The example variable is the one of a provider people know, not the first in the list.
  assert.equal(steps[2].parameters.variable, "CODEALTA_ANTHROPIC_API_KEY");
  assert.equal(translate("en", steps[2].body, steps[2].parameters).includes("such as CODEALTA_ANTHROPIC_API_KEY"), true);
});

test("the tour follows what is configured", () => {
  // Without Codex, another provider that signs in with an account takes its place.
  const copilot = providerTourSteps(defaults.filter(value => value.key !== "codex"));
  assert.deepEqual(copilot.map(step => step.provider), ["copilot", "copilot", null]);
  assert.equal(translate("en", copilot[0].body, copilot[0].parameters),
    "No model provider is enabled yet. Copilot signs in with your GitHub Copilot subscription: there is no API key to create.");
  // A variable set in the configuration is the one the form shows.
  const custom = providerTourSteps([{ ...provider("mine", "openai-chat", "DEFAULT_KEY"), apiKeyEnv: "MY_KEY" }]);
  assert.deepEqual(custom.map(step => step.id), ["others"]);
  assert.equal(custom[0].title, "Set up a provider");
  assert.equal(custom[0].parameters.variable, "MY_KEY");
  // Nothing names a variable: the step says so without an example.
  const bare = providerTourSteps([provider("local", "openai-chat")]);
  assert.equal(translate("en", bare[0].body, bare[0].parameters).includes("{variable}"), false);
  assert.deepEqual(providerTourSteps([]).map(step => step.id), ["others"]);
});

test("the tour starts by itself once, and only while nothing is enabled", () => {
  assert.equal(startsProviderTour(defaults, false), true);
  assert.equal(startsProviderTour(defaults, true), false);
  assert.equal(startsProviderTour([...defaults, provider("mistral", "mistral", null, true)], false), false);
  assert.equal(startsProviderTour([], false), true);
});
