---
title: Copilot enterprise managed settings (#411)
status: in-progress
created: 2026-11-11
summary: Read the managed settings an enterprise gives its Copilot users through GitHub's official Copilot SDK, with its runtime downloaded on demand only for Copilot users, and enforce them in CodeAlta - no bypass mode, the sandbox required - for every session, whatever its provider.
---

# Copilot enterprise managed settings (#411)

Issue [#411](https://github.com/CodeAlta/CodeAlta/issues/411): a Copilot Business enterprise turns off the "YOLO" mode
and requires the sandbox through its managed settings ([reference](https://docs.github.com/en/copilot/reference/enterprise-administrators/enterprise-managed-settings)),
and asks that CodeAlta applies them as VS Code and the Copilot CLI do.

## What we know

- **The policy is set on GitHub** (a `.github-private` repository of the enterprise, `copilot/managed-settings.json`),
  or on the computer by IT (`%ProgramFiles%\GitHubCopilot\managed-settings.json`, `HKLM\SOFTWARE\Policies\GitHubCopilot`,
  the `com.github.copilot` domain on macOS, `/etc/github-copilot/managed-settings.json`). The reporter's is on GitHub.
- **No public REST API gives a user the policy that applies to them**: the AI-controls and team-customization
  endpoints are for enterprise owners and org maintainers. VS Code reads `GET api.github.com/copilot_internal/managed_settings`,
  an internal endpoint: not used here.
- **The official way is the Copilot SDK** (`GitHub.Copilot.SDK`, MIT, already pinned in `src/Directory.Packages.props`
  but referenced by no project). Without a session, `client.Rpc.ManagedSettings.ResolveAsync(selectionId, gitHubToken,
  clientName, workingDirectory)` merges the device channels and the account's server policy, with the runtime's own
  cache (a policy under an hour old is reused, a failed fetch falls back to one up to 24 hours old). Its result gives
  `Resolved.BypassPermissionsDisabled`, `FailClosed`, `ServerManaged`, `DeviceManaged`, `ManagedKeys`,
  `SandboxEnabledByUndeterminedPolicy`, the merged `Resolved.Settings`, typed `Values` with lock state (`Meta`), and a
  `PermissionsContext` for `ManagedSettings.Permissions.EvaluateAsync`. `ReadAsync` gives the device channels alone.
  The surface is marked experimental.
- **It works with CodeAlta's own sign-in**: `ResolveAsync` takes the GitHub token of the direct Copilot provider
  (`CopilotDirectAuth`, GitHub app `Iv1.b507a08c87ecfe98`). The user needs no Copilot CLI sign-in. A probe
  (`dotnet run copilot-policy-probe.cs`, posted on the issue) resolved an account with no policy, as the default CLI
  identity and as `clientName: "CodeAlta"`.
- **Its runtime is the Copilot CLI**, 92-141 MB per platform (`github-copilot-<version>-<platform>.tgz` of the
  `github/copilot-cli` release, with `SHA256SUMS.txt`). It is not embedded in CodeAlta: it is downloaded on demand, as
  `ce866e05d` "Install Copilot CLI on demand" did before `0f68f5043` removed the SDK provider.
- **Not known yet**: whether GitHub delivers an enterprise's policy to `clientName: "CodeAlta"` (the service can refuse a
  client, HTTP 466). The reporter's probe answers it (slice 0).

## Decisions taken here

- **The SDK reads, CodeAlta enforces.** The runtime only answers policy questions; sessions stay with the direct Copilot
  provider. CodeAlta applies the result with its own permission modes and sandbox, as fleet policies already narrow them
  (`FleetPolicyGuard.NarrowerMode`, `SessionFleetFloor`).
- **A policy narrows, never widens**, and applies to **every session of CodeAlta on the computer, whatever its provider**,
  as long as a signed-in Copilot account has one, or the computer has a device policy. A policy limited to Copilot
  sessions would be escaped by switching to Claude Code or Codex. *To confirm with the owner.*
- **The runtime is downloaded on demand, without being asked**, when a Copilot provider is first signed in (or at start
  when one is signed in and the runtime is missing). The version is the one the SDK pins, checked against `SHA256SUMS.txt`,
  kept under `~/.alta/cache/copilot-runtime/<version>/`. Its progress and its failures show on the provider's page.
  Nothing is downloaded for a user without a Copilot provider.
- **The runtime process lives only while it answers**: started off the startup path, it resolves and exits; the policy
  is refreshed at start, at sign-in and sign-out, about every hour, and from a **Refresh** button.
- **The last policy is kept** beside the provider's credential and applies from the next start, offline included, until
  a newer one replaces it. Several Copilot accounts: the most restrictive of their policies applies.
- **CodeAlta follows the runtime's flags exactly** (`BypassPermissionsDisabled`, `SandboxEnabledByUndeterminedPolicy`,
  `FailClosed`); it adds no stricter fallback of its own.
- **`clientName` is `CodeAlta`.**

## What phase 1 enforces

| Managed setting | In CodeAlta |
| --- | --- |
| `permissions.disableBypassPermissionsMode: "disable"` | No mode of rank 4 (`bypassPermissions`): not offered, refused on `alta session create/send --permission-mode`, not given to sub-sessions (`GetCreatedSessionPermissionMode` returns `Bypass` today), not by full trust (`_fullTrust`), nor by the host's "approve" default (`_autoApproveOwnedPermissions`), nor by automations, model-tier fallback or fleet nodes. A session in that mode is narrowed to `acceptEdits` at once. |
| `permissions.disableAssistedPermissionsMode: true` | Claude Code's `auto` (rank 3), the nearest to Copilot's assisted mode, is refused the same way. |
| `permissions.defaultMode` | `manual` -> `default`, `allow-all` -> `bypassPermissions` when allowed, `assisted` -> `default` (CodeAlta has no assisted mode). The user can still choose another allowed mode. |
| `sandbox.enabled: true` | Every session is sandboxed: a scope that says `off`, or none, gives the `workspace` profile; a profile of the user stays when it is set. |
| `sandbox.allowBypass: false` | No command runs outside the sandbox (as **Run outside: Never**). |
| `sandbox.failIfUnavailable: true` | Already CodeAlta's behavior: without a backend, a sandboxed session runs no command. Checked by a test. |
| `forceRemoteSettingsRefresh: true` | While no fresh server policy could be read since the start, sends are refused with the reason. |
| `communication.support.url` | The link of the organization on every locked control. |

## Slices

- [ ] 0. **Gate**: the reporter runs the probe posted on #411. If GitHub delivers their policy to `CodeAlta`, go on; if
      only to the CLI identity, stop and decide with the owner (ask GitHub, or another identity). Slices 1-3 do not wait
      for it; merging does.
- [x] 1. **Runtime on demand** (`CodeAlta.Agent.Copilot`): `CopilotRuntimeInstaller` and `CopilotPinnedRuntime`, on the
      model of today's `CodexAppServerInstaller` (`PinnedDownloader`: lock shared by both instances, `.partial` folder,
      marker) rather than the removed `CopilotCliInstaller`. The pin (1.0.109, the `CopilotCliVersion` of SDK 1.0.31) has
      the size and SHA-256 of each `github-copilot-<version>-<platform>.tgz` in source, as the Codex pin does, instead of
      reading `SHA256SUMS.txt` at run time. It keeps what the SDK's targets keep (the files of `prebuilds/<platform>`
      beside those of the root, without the terminal CLI); tests.
- [x] 2. **Neutral contract** (`CodeAlta.Orchestration`): `ManagedPolicy` (source, account, refused modes with
      `Narrow` to the widest mode the session can be given, default mode, sandbox required, no run outside, fresh
      policy missing, support URL, managed keys, read at) and `ManagedPolicyState` (one policy per source, combined
      most-restrictive, `Changed`), instance-owned; tests.
- [x] 3. **The Copilot reader**: `CopilotManagedSettingsReader` and `CopilotManagedSettings` (`CodeAlta.Agent.Copilot`,
      SDK 1.0.31, `CopilotSkipCliDownload`, `GHCP001` suppressed) start the runtime per reading in a home of its own
      (`UseLoggedInUser = false`) and resolve each token with `clientName: "CodeAlta"`, or the computer alone without
      one. `CopilotManagedPolicySource` (`CodeAlta.Hosting`, which sees the providers) lists the Copilot providers,
      reads their GitHub tokens, installs the runtime only when one exists, maps to `ManagedPolicy`, keeps the last
      policy in `~/.alta/auth/copilot-policy/` and refreshes hourly or on demand. *Deviation*: `ManagedPolicy` moved
      to `CodeAlta.Agent`, the project both `CodeAlta.Hosting` and `CodeAlta.Orchestration` see. Tests, and a live
      test (`CODEALTA_COPILOT_POLICY_LIVE_TOKEN`) that passed with a personal account (no policy).
      *Not done*: the Copilot ACP provider's own sign-in (the runtime would have to read the user's Copilot CLI
      home); an ACP provider gets the policy of the direct providers and of the computer.
- [x] 4. **Permission modes**: `SessionRuntimeService.ManagedPolicies` (one `ManagedPolicyState` per runtime), filled by
      `CodeAltaHostOptions.StartManagedPolicies`, which the desktop, the TUI, `altacli` and `altanode` set. The refused
      modes are offered nowhere (choices, new sessions, `alta`), a session attached in one is narrowed at once, the mode
      given to the provider is narrowed (a provider configured in a refused mode gets an explicit one), and the host's own
      approval, full trust and the mode of created sessions approve nothing the policy refuses; `alta session create`
      names the policy. Automations, model-tier fallback and fleet nodes go through these points. Tests.
- [x] 5. **Sandbox**: `ResolveSandboxAsync` gives `workspace` (`SandboxScope.Organization`) to a session nothing sandboxes
      where the sandbox can run (also where it cannot, with `failIfUnavailable`), and `Outside = Never` with
      `allowBypass: false`; a send is settled `policy_refused` while a policy that must be read again was not. Tests.
- [x] 6. **CodeAlta Desktop**: the `managedPolicy` RPC service; the Copilot provider's page has **Policy of your
      organization** (what it enforces, the download of the runtime, a failure with **Try again**, **Refresh the policy**,
      **Who to ask**); Settings > Permissions and Sandbox show a lock on what it decides; the Agent chip and
      `alta session choices` do not offer the refused modes (from the runtime); a refused prompt says why
      (`policy_refused`). Checked in the developer instance (dark theme); the TUI gets the enforcement, no new screen.
- [x] 7. **Developer check without an enterprise**: `CODEALTA_DEV_COPILOT_MANAGED_SETTINGS`, read only with `--dev`,
      names a `managed-settings.json` the runtime composes as the server layer (`ManagedSettings.ComposeAsync`), combined
      most-restrictive with what the accounts resolve, never kept on disk: it can only narrow. Checked with the user's
      Copilot account: the runtime was downloaded, the policy read and every lock shown.
- [x] 8. **Documentation**: "Policy of your organization" in `site/docs/model-providers/setup.md`, linked from the
      permission modes and the sandbox; `doc/runtime.md` (tools and permissions); `readme.md`.
- [ ] 9. **Validation**: .NET tests, `npm test`, `lunet build`; the reporter confirms on their enterprise.

## Not in this plan

- `permissions.allow/ask/deny` rules (through `ManagedSettings.Permissions.EvaluateAsync` on each request of
  `SessionPermissionService`), `allowedMcpServers`/`deniedMcpServers`, the sandbox's `userPolicy` paths and network,
  `model`/`effortLevel` defaults: a phase 2 plan once phase 1 is confirmed on a real enterprise.
- An assisted mode of CodeAlta's own (the issue's side request): its own issue.
- `enabledPlugins`, marketplaces, `telemetry` (OpenTelemetry export), `remoteControl`, `features.computerUse`,
  `sandboxMcpServers`, `sandboxLspServers`, `auth.git/gh`: Copilot-specific or with no counterpart in CodeAlta yet.
