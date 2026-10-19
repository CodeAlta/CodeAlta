import assert from "node:assert/strict";
import test from "node:test";
import { cliLoginCommand, cliPresets, isSecretName, newProfile, profileItems, profileProblem, profilesValid, readProfiles, sameProfiles, type CliProfileCard } from "./cliProfiles";
import { cliRows } from "./SessionIdentityInfo";

const card = (name: string, env: [string, string][], check = "", login = ""): CliProfileCard =>
  ({ key: name, name, env: env.map(([variable, value]) => ({ name: variable, value })), check, login });

test("the CLI profiles are read from the host in their order, and go back trimmed without the lines left empty", () => {
  const cards = readProfiles([{ name: "azure", env: [{ name: "AZURE_CONFIG_DIR", value: "~/.azure-contoso" }], check: "az account show", login: null },
    { name: "kubectl", env: null, check: null, login: null }]);
  assert.deepEqual(cards.map(item => [item.name, item.env, item.check, item.login]),
    [["azure", [{ name: "AZURE_CONFIG_DIR", value: "~/.azure-contoso" }], "az account show", ""], ["kubectl", [], "", ""]]);
  assert.notEqual(cards[0].key, cards[1].key);
  assert.deepEqual(profileItems([card(" aws ", [["AWS_PROFILE", " contoso "], ["", " "]], " ", "aws sso login")]),
    [{ name: "aws", env: [{ name: "AWS_PROFILE", value: "contoso" }], check: null, login: "aws sso login" }]);
  assert.equal(sameProfiles([card("aws", [["AWS_PROFILE", "contoso"]])], [card("aws", [["AWS_PROFILE", "contoso "], ["", ""]])]), true);
  assert.equal(sameProfiles([card("aws", [["AWS_PROFILE", "contoso"]])], []), false);
});

test("a profile that cannot be saved says why, as the host would", () => {
  const ok = card("azure", [["AZURE_CONFIG_DIR", "~/.azure-contoso"]]);
  assert.equal(profileProblem([ok], 0), null);
  assert.deepEqual(profileProblem([card("", [["A", "1"]])], 0), { kind: "name" });
  assert.deepEqual(profileProblem([card("my tool", [["A", "1"]])], 0), { kind: "name" });
  assert.deepEqual(profileProblem([ok, card("Azure", [["B", "1"]])], 1), { kind: "duplicateName" });
  assert.deepEqual(profileProblem([card("x", [["", ""]])], 0), { kind: "noVariable" });
  assert.deepEqual(profileProblem([card("x", [["1A", "v"]])], 0), { kind: "variableName", variable: "1A" });
  assert.deepEqual(profileProblem([card("x", [["GH_CONFIG_DIR", "~/gh"]])], 0), { kind: "reserved", variable: "GH_CONFIG_DIR" });
  assert.deepEqual(profileProblem([card("x", [["GIT_CONFIG_KEY_0", "user.name"]])], 0), { kind: "reserved", variable: "GIT_CONFIG_KEY_0" });
  assert.deepEqual(profileProblem([card("x", [["A", "1"], ["a", "2"]])], 0), { kind: "duplicateVariable", variable: "a" });
  assert.deepEqual(profileProblem([card("x", [["A", ""]])], 0), { kind: "value", variable: "A" });
  assert.deepEqual(profileProblem([card("x", [["A", "${HOME}/x"]])], 0), { kind: "reference", variable: "A" });
  assert.equal(profileProblem([card("x", [["A", "${env:HOME}/x"]])], 0), null);
  assert.deepEqual(profileProblem([card("vault", [["VAULT_TOKEN", "hvs.secret"]])], 0), { kind: "secret", variable: "VAULT_TOKEN" });
  assert.equal(profileProblem([card("vault", [["VAULT_TOKEN", "${env:CONTOSO_VAULT_TOKEN}"]])], 0), null, "A secret is given by the variable that holds it.");
  // Two profiles may give a variable the same value, never two values.
  const helm = card("helm", [["KUBECONFIG", "~/.kube/contoso"]]);
  assert.equal(profileProblem([card("kubectl", [["KUBECONFIG", "~/.kube/contoso"]]), helm], 1), null);
  assert.deepEqual(profileProblem([card("kubectl", [["KUBECONFIG", "~/.kube/other"]]), helm], 1), { kind: "conflict", variable: "KUBECONFIG", other: "kubectl" });
  assert.deepEqual(profileProblem([card("x", [["A", "a\tb"]])], 0), { kind: "value", variable: "A" }, "A value is one line, without control characters.");
  assert.deepEqual(profileProblem([card("x", [["A", "1"]], "az\naccount")], 0), { kind: "check" });
  assert.deepEqual(profileProblem([card("x", [["A", "1"]], "", "x".repeat(2049))], 0), { kind: "login" });
  assert.equal(profileProblem([card("azure", [["AZURE_FEDERATED_TOKEN_FILE", "~/.azure/token"]])], 0), null, "A name of a place holds where a secret is.");
  assert.equal(profilesValid([ok, helm]), true);
  assert.equal(profilesValid([ok, card("", [])]), false);
});

test("a name says it holds a secret by its words, not by a part of a word", () => {
  for (const name of ["VAULT_TOKEN", "AWS_SECRET_ACCESS_KEY", "ARM_CLIENT_SECRET", "OPENAI_API_KEY", "DB_PASSWORD", "GITHUB_PAT", "STORAGE_ACCOUNT_KEY"]) {
    assert.equal(isSecretName(name), true, name);
  }
  for (const name of ["PATH", "KUBECONFIG", "AWS_PROFILE", "AWS_SHARED_CREDENTIALS_FILE", "TOKENIZER_DIR", "SSH_KEY_PATH"]) {
    assert.equal(isSecretName(name), false, name);
  }
});

test("a preset fills a profile with the space in its paths, and every preset can be saved", () => {
  const azure = newProfile(cliPresets.find(preset => preset.id === "azure")!, "work");
  assert.deepEqual([azure.name, azure.env, azure.login], ["azure", [{ name: "AZURE_CONFIG_DIR", value: "~/.azure-work" }], "az login"]);
  assert.equal(profilesValid(cliPresets.map(preset => newProfile(preset, "work"))), true);
  assert.deepEqual(newProfile(null, "work").env, [{ name: "", value: "" }]);
});

test("the login command of a profile sets its variables for the shell of the platform", () => {
  const azure = card("azure", [["AZURE_CONFIG_DIR", "~/.azure-o'brien"], ["AZURE_CORE_OUTPUT", "${env:CONTOSO_OUTPUT}"]], "", "az login --tenant contoso");
  assert.equal(cliLoginCommand(azure, "/home/me", "linux"),
    "AZURE_CONFIG_DIR='/home/me/.azure-o'\\''brien' AZURE_CORE_OUTPUT=\"${CONTOSO_OUTPUT}\" az login --tenant contoso");
  assert.equal(cliLoginCommand(azure, "C:\\Users\\me", "windows"),
    "$env:AZURE_CONFIG_DIR='C:\\Users\\me\\.azure-o''brien'; $env:AZURE_CORE_OUTPUT=$env:CONTOSO_OUTPUT; az login --tenant contoso");
  const list = card("kubectl", [["KUBECONFIG", "~/.kube/a:~/.kube/${env:USER}"]], "", "kubectl version");
  assert.equal(cliLoginCommand(list, "/home/me", "macos"), "KUBECONFIG='/home/me/.kube/a:/home/me/.kube/'\"${USER}\" kubectl version");
  const after = card("kubectl", [["KUBECONFIG", "${env:BASE}~/a:~/b"]], "", "kubectl version");
  assert.equal(cliLoginCommand(after, "/home/me", "linux"), "KUBECONFIG=\"${BASE}\"'~/a:/home/me/b' kubectl version",
    "~ right after a reference goes on from it; after the separator of a list, it is the home folder.");
  assert.equal(cliLoginCommand(card("aws", [["AWS_PROFILE", "contoso"]]), "/home/me", "linux"), null, "Without a login command, nothing to copy.");
});

test("the information window of a session shows its CLI profiles as they are written", () => {
  const reply = { status: "ok", spaceId: "work", gitAuthor: null, signing: null, signOff: false, gitHubProfile: null, jiraSite: null, jiraEmail: null,
    cli: [{ name: "azure", source: "space", env: [{ name: "AZURE_CONFIG_DIR", value: "~/.azure-contoso" }], unset: [] },
      { name: "kubectl", source: "project", env: [{ name: "KUBECONFIG", value: "${env:PROD_KUBECONFIG}" }], unset: ["KUBECONFIG"] }] };
  assert.deepEqual(cliRows(reply), [
    { name: "azure", project: false, unset: [], variables: "AZURE_CONFIG_DIR=~/.azure-contoso" },
    { name: "kubectl", project: true, unset: ["KUBECONFIG"], variables: "KUBECONFIG=${env:PROD_KUBECONFIG}" }]);
  assert.deepEqual(cliRows({ ...reply, status: "stale_epoch" }), []);
});
