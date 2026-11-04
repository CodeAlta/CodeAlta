import test from "node:test";
import assert from "node:assert/strict";
import type { FleetSignInApprovalView, FleetSignInSettingsView } from "#neoastra";
import { allowedValues, approvalLines, confirmWays, isEntraIssuer, orderedSignInMethods, removableLogin, signInCode, signInDraft, signInDraftProblem, signInProblem, signInSummary } from "./signIn";
const settings = (look: Partial<FleetSignInSettingsView> = {}): FleetSignInSettingsView => ({ methods: [
  { id: "company", name: "Contoso", enabled: true, available: true }, { id: "passkey", name: "Passkey", enabled: true, available: true },
  { id: "microsoft", name: "Microsoft", enabled: true, available: false }, { id: "google", name: "Google", enabled: false, available: true },
  { id: "github", name: "GitHub", enabled: true, available: true }], issuer: "https://login.contoso.example", clientId: "client", hasClientSecret: true, displayName: "Contoso",
  groupsClaim: "groups", groupRoles: [{ group: "g-admins", role: "admin" }], requireSso: false, version: 3, updatedAt: null, allowedDomains: ["contoso.example"], allowedTenants: [],
  ...look });

test("the groups that give roles name each group once, by its id", () => {
  const draft = signInDraft(settings());
  assert.deepEqual(draft.groupRoles, [{ group: "g-admins", role: "admin" }]);
  assert.equal(signInDraftProblem({ ...draft, groupRoles: [...draft.groupRoles, { group: " ", role: "member" }] }), "Each group needs its id.");
  assert.equal(signInDraftProblem({ ...draft, groupRoles: [...draft.groupRoles, { group: "g-admins ", role: "auditor" }] }), "A group gives one role.");
});

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

test("company sign-in names the domains or the tenants it lets in", () => {
  const draft = signInDraft(settings());
  assert.equal(draft.allowedDomains, "contoso.example");
  assert.equal(signInDraftProblem({ ...draft, allowedDomains: " , " }), "Company sign-in needs the email domains or the tenant ids it lets in.");
  assert.equal(signInDraftProblem({ ...draft, allowedDomains: "", allowedTenants: "tenant-a" }), null);
  assert.deepEqual(allowedValues(" Contoso.com, fabrikam.com;contoso.com\nexample.org "), ["contoso.com", "fabrikam.com", "example.org"]);
});

test("Entra ID is told by its issuer's host, whatever the tenant or the path", () => {
  for (const issuer of ["https://login.microsoftonline.com/organizations/v2.0", " https://LOGIN.microsoftonline.com/0a1b/v2.0 ", "https://sts.windows.net/0a1b/",
    "https://login.microsoft.com/0a1b/v2.0"]) assert.equal(isEntraIssuer(issuer), true, issuer);
  for (const issuer of ["", "not a url", "https://login.contoso.example/v2.0", "https://accounts.google.com", "https://evil.example/login.microsoftonline.com"]) assert.equal(isEntraIssuer(issuer), false, issuer);
});

test("a confirmation with an account that is not theirs names the ways that are: the accounts of that method first, then each other method once", () => {
  const login = (method: string, name: string) => ({ method, name });
  const name = (method: string) => ({ passkey: "Passkey", microsoft: "Microsoft", google: "Google", github: "GitHub" })[method] ?? method;
  assert.deepEqual(confirmWays("microsoft", [login("google", "a@gmail.com"), login("passkey", "Passkey on Windows"), login("microsoft", "alex@contoso.example"), login("github", "alex"),
    login("microsoft", "alex@live.com"), login("passkey", "Passkey on Mac")], name), ["alex@contoso.example", "alex@live.com", "Google", "Passkey", "GitHub"]);
  assert.deepEqual(confirmWays("github", [login("microsoft", "alex@contoso.example")], name), ["Microsoft"], "none of that method linked: only the others");
  assert.deepEqual(confirmWays("passkey", [login("passkey", "Passkey on Windows"), login("google", "a@gmail.com")], name), ["Passkey", "Google"], "a passkey is named, not its device");
  assert.deepEqual(confirmWays("microsoft", [], name), []);
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
  assert.equal(signInProblem("sign_in.last_way_in"), "This is your last way to sign in, and you have no recovery key: make your recovery key first, or link another way.");
  assert.equal(signInProblem("sign_in.managed_by_organization"), "Managed by your organization: an admin removes a person in People and access");
  assert.equal(signInProblem("sign_in.login_not_found"), "This way to sign in was removed already.");
});

test("a linked account or a passkey is removed from the row; a company account is the organization's, and an older server gives no id", () => {
  const login = (method: string, id: string | null = "a_1") => ({ method, name: "alex@example.com", createdAt: "2026-11-04T08:00:00Z", lastUsedAt: null, id });
  for (const method of ["passkey", "microsoft", "google", "github"]) assert.equal(removableLogin(login(method)), true, method);
  assert.equal(removableLogin(login("company")), false);
  assert.equal(removableLogin(login("microsoft", null)), false);
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

test("a change to approve shows every field that decides who signs in, and what it turns off", () => {
  const approval = (look: Partial<FleetSignInApprovalView> = {}): FleetSignInApprovalView => ({ id: "a", requestedBy: "Office", issuer: "https://login.contoso.example",
    clientId: "client", secretChanges: false, allowedDomains: ["contoso.example"], allowedTenants: [], requireSso: false, enabled: ["company", "passkey"],
    expiresAt: "2026-11-04T12:10:00Z", digest: "d", displayName: null, groupsClaim: null, groupRoles: null, secretRemoved: false, breakGlassPersonId: null, companyOff: false,
    ...look });
  const text = (lines: ReturnType<typeof approvalLines>) => lines.map(line => line.value === undefined ? line.key : `${line.key}: ${line.value}`);
  assert.deepEqual(text(approvalLines(approval({ groupRoles: [{ group: "g-admins", role: "admin" }], groupsClaim: "roles", requireSso: true, breakGlassPersonId: "p-2",
    secretChanges: true }), null)), ["Issuer: https://login.contoso.example · client", "Groups claim: roles", "Email domains let in: contoso.example",
    "Roles from groups: g-admins → admin", "Sign in with: company, passkey", "Require company sign-in", "Break-glass owner: p-2", "A new client secret"]);
  // Required company sign-in turned off, the roles of the groups removed, the secret removed: each is said.
  assert.deepEqual(text(approvalLines(approval({ groupRoles: [], secretChanges: true, secretRemoved: true }), settings({ requireSso: true }))).slice(-4),
    ["Roles from groups removed", "Sign in with: company, passkey", "Company sign-in no longer required", "Client secret removed"]);
  assert.deepEqual(text(approvalLines(approval({ companyOff: true, enabled: ["passkey"] }), null)).slice(0, 3),
    ["Issuer: https://login.contoso.example · client", "Groups claim: groups", "Company sign-in turned off"]);
  assert.equal(text(approvalLines(approval({ issuer: null, clientId: null, allowedDomains: [], companyOff: true, enabled: ["passkey"] }), null))[0], "Company sign-in removed");
});
