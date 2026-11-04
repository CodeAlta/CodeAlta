import test from "node:test";
import assert from "node:assert/strict";
import type { FleetSignInSettingsView } from "#neoastra";
import { orderedSignInMethods, signInCode, signInDraft, signInDraftProblem, signInProblem, signInSummary } from "./signIn";

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
  assert.equal(signInProblem("sign_in.denied"), "The sign-in was refused at the provider.");
  assert.equal(signInProblem("sign_in.expired"), "The code expired before it was entered: start the sign-in again.");
  assert.equal(signInProblem("unreachable"), null);
});

test("a GitHub code is shown only when it is a short printable code with an https:// page", () => {
  const handle = "0123456789abcdef0123456789abcdef";
  const code = (userCode: string, url: string, id = handle) => signInCode({ handle: id, userCode, url, expiresAt: "2026-11-04T12:15:00Z" });
  assert.deepEqual(code("WDJB-MJHT", "https://github.com/login/device"), { handle, userCode: "WDJB-MJHT", url: "https://github.com/login/device" });
  assert.deepEqual(code("WDJB-MJHT", "http://127.0.0.1:5000/login/device"), { handle, userCode: "WDJB-MJHT", url: "http://127.0.0.1:5000/login/device" }, "a fake GitHub of a test");
  assert.equal(code("WDJB-MJHT", "https://github.com/login/device", "not a handle"), null);
  assert.equal(code("WDJB-MJHT", "http://github.example/login/device"), null);
  assert.equal(code("WDJB-MJHT", "javascript:alert(1)"), null);
  assert.equal(code("two words", "https://github.com/login/device"), null);
  assert.equal(code("", "https://github.com/login/device"), null);
  assert.equal(signInCode(null), null);
});
