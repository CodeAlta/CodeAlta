import test from "node:test";
import assert from "node:assert/strict";
import type { FleetSignInSettingsView } from "#neoastra";
import { orderedSignInMethods, signInDraft, signInDraftProblem, signInProblem, signInSummary } from "./signIn";

const settings = (look: Partial<FleetSignInSettingsView> = {}): FleetSignInSettingsView => ({ methods: [
  { id: "company", name: "Contoso", enabled: true, available: true }, { id: "passkey", name: "Passkey", enabled: true, available: true },
  { id: "microsoft", name: "Microsoft", enabled: true, available: false }, { id: "google", name: "Google", enabled: false, available: true },
  { id: "github", name: "GitHub", enabled: true, available: true }], issuer: "https://login.contoso.example", clientId: "client", hasClientSecret: true, displayName: "Contoso",
  groupsClaim: "groups", requireSso: false, version: 3, updatedAt: null, ...look });

test("the company's sign-in comes first, then a passkey, then the linked accounts", () => {
  assert.deepEqual(orderedSignInMethods([{ id: "github", name: "GitHub", breakGlass: false }, { id: "passkey", name: "Passkey", breakGlass: true },
    { id: "company", name: "Contoso", breakGlass: false }]).map(method => method.id), ["company", "passkey", "github"]);
});

test("a draft starts from the settings held, without the secret", () => {
  const draft = signInDraft(settings());
  assert.deepEqual([draft.enabled, draft.issuer, draft.clientId, draft.clientSecret, draft.requireSso],
    [["company", "passkey", "microsoft", "github"], "https://login.contoso.example", "client", "", false]);
});

test("company sign-in needs an https issuer and a client id, and requiring it needs it on", () => {
  const draft = signInDraft(settings());
  assert.equal(signInDraftProblem(draft), null);
  assert.equal(signInDraftProblem({ ...draft, issuer: "http://idp.example" }), "The issuer is an https:// address.");
  assert.equal(signInDraftProblem({ ...draft, issuer: "http://127.0.0.1:5000" }), null, "a provider on this computer, for tests");
  assert.equal(signInDraftProblem({ ...draft, clientId: " " }), "Company sign-in needs its client id.");
  assert.equal(signInDraftProblem({ ...draft, issuer: "", clientId: "" }), "Company sign-in needs its issuer and client id.");
  assert.equal(signInDraftProblem({ ...draft, issuer: "", clientId: "", enabled: ["passkey"], requireSso: true }), "Requiring company sign-in needs company sign-in on.");
  assert.equal(signInDraftProblem({ ...draft, enabled: ["passkey"], requireSso: true }), "Requiring company sign-in needs company sign-in on.");
});

test("the row says the methods people may use now, or that company sign-in is required", () => {
  assert.deepEqual(signInSummary(settings()), { key: "Sign in with {methods}", methods: "Contoso, Passkey, GitHub" }, "Microsoft is not registered, Google is off");
  assert.equal(signInSummary(settings({ requireSso: true })).key, "Company sign-in required");
  assert.equal(signInSummary(settings({ methods: [] })).key, "No sign-in: enrollment codes only");
});

test("the refusals of sign-in say what to do, and the other statuses are left to the server's", () => {
  assert.equal(signInProblem("sign_in.sso_required"), "Your organization requires its company sign-in.");
  assert.equal(signInProblem("sign_in.wrong_nonce"), "The sign-in did not verify: start it again.");
  assert.equal(signInProblem("sign_in.cancelled"), "The sign-in was cancelled.");
  assert.equal(signInProblem("unreachable"), null);
});
