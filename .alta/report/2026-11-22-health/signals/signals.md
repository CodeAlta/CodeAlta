# Signals of CodeAlta at 2a3fd8388

History since 4 weeks ago. Leads to read in the code, not findings. Full lists: `signals.json`.

- 6123 text files, 1355472 lines (858 files left out as generated, vendored or agent work files); 1702 commits, 902 of them fixes (by their subject), 56 of those without a change to a test; 3908 files changed.
- Uncommitted changes in 18 paths: sizes, words and markers are those of the working folder, not of 2a3fd8388.
- Tests: 983 sleep-like calls, 5 skips, 126 mentions of retry or flaky.
- Code: 26 TODO/FIXME/HACK, 476 mentions of a workaround. Docs: 326536 words.

## Areas by activity

| area | files | lines | commits | churn | fixes | fixes without a test |
| --- | --- | --- | --- | --- | --- | --- |
| `src/CodeAlta` | 2194 | 337672 | 722 | 158460 | 326 | 23 |
| `site/docs` | 65 | 11722 | 443 | 6615 | 138 | 0 |
| `src/CodeAlta.Desktop.Tests` | 277 | 73615 | 398 | 28192 | 184 | 0 |
| `src/CodeAlta.Tests` | 398 | 159539 | 335 | 34779 | 158 | 0 |
| `src/CodeAlta.Orchestration` | 211 | 56238 | 276 | 28381 | 132 | 5 |
| `src/CodeAlta.Fleet` | 135 | 32725 | 247 | 37744 | 149 | 4 |
| `src/CodeAlta.Server.Tests` | 55 | 15538 | 205 | 16251 | 128 | 0 |
| `src/CodeAlta.Catalog` | 339 | 74433 | 193 | 25917 | 62 | 1 |
| `src/CodeAlta.Node` | 52 | 12713 | 178 | 15298 | 75 | 3 |
| `doc` | 18 | 3751 | 169 | 1853 | 49 | 0 |
| `doc/fleet` | 19 | 4162 | 167 | 5550 | 63 | 0 |
| `src/CodeAlta.Orchestration.Tests` | 121 | 34671 | 165 | 14487 | 89 | 0 |
| `src/CodeAlta.Server` | 125 | 26277 | 164 | 28505 | 87 | 5 |
| `src/CodeAlta.Node.Tests` | 51 | 10605 | 159 | 11459 | 77 | 0 |
| `src/CodeAlta.LiveTool` | 113 | 37282 | 156 | 17977 | 54 | 3 |
| `src/CodeAlta.Agent` | 220 | 49078 | 143 | 12206 | 61 | 0 |
| `src/CodeAlta.Catalog.Tests` | 110 | 26972 | 98 | 10009 | 42 | 0 |
| `src/CodeAlta.Fleet.Protocol` | 43 | 10816 | 98 | 11241 | 34 | 0 |
| `src/CodeAlta.Fleet.Tests` | 35 | 5654 | 81 | 5794 | 52 | 0 |
| `src/CodeAlta.Automations` | 32 | 10158 | 72 | 7801 | 27 | 1 |

## Hotspots: changed often and large

| file | commits | lines | fixes | last change |
| --- | --- | --- | --- | --- |
| `src/CodeAlta/frontend/src/main.tsx` | 144 | 8080 | 38 | 2026-11-20 |
| `src/CodeAlta/frontend/src/localization.ts` | 113 | 3240 | 16 | 2026-11-20 |
| `src/CodeAlta/Desktop/DesktopApplication.cs` | 115 | 1934 | 24 | 2026-11-22 |
| `src/CodeAlta/Desktop/Rpc/BootRpc.cs` | 109 | 1173 | 9 | 2026-11-20 |
| `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs` | 81 | 7170 | 35 | 2026-11-22 |
| `src/CodeAlta/frontend/src/style.css` | 87 | 3671 | 23 | 2026-11-20 |
| `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs` | 63 | 8526 | 15 | 2026-11-19 |
| `src/CodeAlta.Orchestration/Hosting/CodeAltaHost.cs` | 72 | 1608 | 26 | 2026-11-22 |
| `src/CodeAlta.Node/NodeFleet.cs` | 57 | 603 | 15 | 2026-11-16 |
| `src/CodeAlta.Agent/Runtime/AgentSession.cs` | 42 | 5859 | 18 | 2026-11-22 |
| `src/CodeAlta.Fleet/NodeApi/FleetNodeService.cs` | 51 | 790 | 17 | 2026-11-16 |
| `src/CodeAlta.Fleet/NodeApi/FleetNodeContracts.cs` | 42 | 1344 | 11 | 2026-11-22 |
| `src/CodeAlta.Node/NodeAutomations.cs` | 43 | 1092 | 13 | 2026-11-20 |
| `src/CodeAlta.Tests/CodeModeToolTests.cs` | 39 | 1817 | 14 | 2026-11-22 |
| `src/CodeAlta.Server/Hosting/CoordinatorServer.cs` | 45 | 644 | 12 | 2026-11-13 |
| `src/CodeAlta.Catalog/CodeAltaConfigStore.cs` | 34 | 4423 | 4 | 2026-11-17 |
| `src/CodeAlta/frontend/src/demo-api.ts` | 45 | 435 | 3 | 2026-11-20 |
| `src/CodeAlta/Desktop/Fleet/DesktopFleetServers.cs` | 37 | 1047 | 10 | 2026-11-11 |
| `src/CodeAlta/frontend/src/OwnedSessionPanel.tsx` | 34 | 1855 | 14 | 2026-11-22 |
| `src/CodeAlta.Fleet/Server/FleetConnector.cs` | 36 | 1199 | 15 | 2026-11-13 |

## Fixed most often

| file | fixes | commits | lines |
| --- | --- | --- | --- |
| `src/CodeAlta/frontend/src/main.tsx` | 38 | 144 | 8080 |
| `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs` | 35 | 81 | 7170 |
| `src/CodeAlta.Orchestration/Hosting/CodeAltaHost.cs` | 26 | 72 | 1608 |
| `src/CodeAlta/Desktop/DesktopApplication.cs` | 24 | 115 | 1934 |
| `src/CodeAlta/frontend/src/style.css` | 23 | 87 | 3671 |
| `src/CodeAlta.Fleet/Server/FleetBudgetGate.cs` | 20 | 21 | 695 |
| `src/CodeAlta.Agent/Runtime/AgentSession.cs` | 18 | 42 | 5859 |
| `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.SandboxTools.cs` | 18 | 33 | 703 |
| `src/CodeAlta.Fleet/NodeApi/FleetNodeService.cs` | 17 | 51 | 790 |
| `src/CodeAlta/frontend/src/localization.ts` | 16 | 113 | 3240 |
| `src/CodeAlta.Orchestration.Tests/SessionSandboxTests.cs` | 16 | 30 | 1193 |
| `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs` | 15 | 63 | 8526 |
| `src/CodeAlta.Node/NodeFleet.cs` | 15 | 57 | 603 |
| `src/CodeAlta.Fleet/Server/FleetConnector.cs` | 15 | 36 | 1199 |
| `src/CodeAlta.Tests/CodeModeToolTests.cs` | 14 | 39 | 1817 |
| `src/CodeAlta/frontend/src/OwnedSessionPanel.tsx` | 14 | 34 | 1855 |
| `src/CodeAlta.Fleet/NodeApi/FleetNodeService.Budgets.cs` | 14 | 17 | 364 |
| `src/CodeAlta.Server.Tests/FleetSecretsTests.cs` | 14 | 16 | 912 |
| `src/CodeAlta.Node/NodeAutomations.cs` | 13 | 43 | 1092 |
| `src/CodeAlta.Tests/AltaLiveToolTests.cs` | 13 | 27 | 8003 |

## Files of different areas that change together

Shared commits, their share of the commits of the two files, and of the file that changes less; commits of more than 30 files are left out of both. A high share across areas is a dependency the structure does not show.

| file | file | shared | of both | of the smaller |
| --- | --- | --- | --- | --- |
| `src/CodeAlta.Plugin.Mobile/MobileServer.cs` | `src/CodeAlta/frontend/src/mobile/api.ts` | 11 | 92% | 92% |
| `site/docs/issues.md` | `src/CodeAlta/Desktop/Rpc/IssuesRpc.cs` | 5 | 91% | 100% |
| `src/CodeAlta.Plugin.Mobile/MobileServer.cs` | `src/CodeAlta/frontend/src/mobile/fixtureServer.ts` | 10 | 80% | 83% |
| `src/CodeAlta.Fleet.Protocol/FleetSignInApiContracts.cs` | `src/CodeAlta.Server/Identity/SignIn/SignInAttempts.cs` | 8 | 80% | 89% |
| `src/CodeAlta.Fleet/Server/FleetServerClient.SignIn.cs` | `src/CodeAlta/Desktop/Fleet/DesktopFleetServers.SignIn.cs` | 8 | 80% | 89% |
| `src/CodeAlta.Plugins.Abstractions/PluginContributions.cs` | `src/CodeAlta.Plugins/PluginRuntimeLifecycle.cs` | 5 | 77% | 100% |
| `src/CodeAlta.Fleet.Protocol/FleetSignInApiContracts.cs` | `src/CodeAlta/Desktop/Rpc/FleetServerRpc.SignIn.cs` | 8 | 76% | 89% |
| `src/CodeAlta.Fleet/Server/FleetServerClient.SignIn.cs` | `src/CodeAlta/Desktop/Rpc/FleetServerRpc.SignIn.cs` | 8 | 76% | 89% |
| `src/CodeAlta.Fleet.Protocol/FleetAccessDocuments.cs` | `src/CodeAlta.Server/Services/AccessDocumentService.cs` | 9 | 75% | 82% |
| `src/CodeAlta.LiveTool/AltaAutomations.cs` | `src/CodeAlta/Desktop/DesktopAltaAutomations.cs` | 6 | 75% | 100% |
| `site/docs/plugins/mobile.md` | `src/CodeAlta/frontend/src/mobile/app.ts` | 20 | 71% | 77% |
| `src/CodeAlta.Fleet.Protocol/FleetAccessApiContracts.cs` | `src/CodeAlta.Fleet/Server/FleetGrantKeys.cs` | 5 | 71% | 83% |
| `src/CodeAlta.Fleet.Protocol/FleetAccessApiContracts.cs` | `src/CodeAlta.Server/Storage/IAccessStore.cs` | 5 | 71% | 83% |
| `src/CodeAlta.Fleet.Protocol/FleetAccessApiContracts.cs` | `src/CodeAlta.Server/Storage/Sqlite/SqliteCoordinatorStore.Access.cs` | 5 | 71% | 83% |
| `src/CodeAlta.LiveTool/IAltaFleet.cs` | `src/CodeAlta/Desktop/Fleet/DesktopAltaFleet.cs` | 5 | 71% | 71% |
| `src/CodeAlta.Plugin.Mobile/MobileServer.cs` | `src/CodeAlta/frontend/mobile.html` | 8 | 70% | 73% |
| `src/CodeAlta.Server/Services/SignInService.cs` | `src/CodeAlta/frontend/src/fleet/signInMessages.ts` | 12 | 69% | 75% |
| `site/docs/plugins/mobile.md` | `src/CodeAlta/frontend/src/mobile/mobile.css` | 18 | 68% | 78% |
| `src/CodeAlta.Node/NodeAutomations.Server.cs` | `src/CodeAlta.Server/Services/FleetAutomationService.cs` | 16 | 68% | 73% |
| `site/docs/plugins/developers.md` | `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md` | 10 | 67% | 71% |

## Largest files

| file | lines | commits |
| --- | --- | --- |
| `src/CodeAlta.Pseudonymization.Tests/Presidio/presidio-cases.json` | 10700 | 1 |
| `src/CodeAlta.Catalog/SR.yml` | 9544 | 5 |
| `src/CodeAlta.Tests/OpenAIRawApiModelProviderRuntimeTests.cs` | 8621 | 18 |
| `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.cs` | 8526 | 63 |
| `src/CodeAlta/frontend/src/main.tsx` | 8080 | 144 |
| `src/CodeAlta.Tests/AltaLiveToolTests.cs` | 8003 | 27 |
| `src/CodeAlta.Orchestration/Runtime/SessionRuntimeService.cs` | 7170 | 81 |
| `src/CodeAlta.Tests/AgentSessionTests.cs` | 6417 | 13 |
| `src/CodeAlta.Agent/Runtime/AgentSession.cs` | 5859 | 42 |
| `src/CodeAlta.Agent.OpenAI/OpenAIResponsesTurnExecutor.cs` | 4680 | 17 |
| `src/CodeAlta.Catalog/CodeAltaConfigStore.cs` | 4423 | 34 |
| `src/CodeAlta.Tests/CodeAltaAppTests.cs` | 3692 | 0 |
| `src/CodeAlta/frontend/src/style.css` | 3671 | 87 |
| `src/CodeAlta.Orchestration.Tests/OwnedSessionCommandServiceTests.cs` | 3339 | 13 |
| `src/CodeAlta/frontend/src/localization.ts` | 3240 | 113 |
| `src/CodeAlta.Tests/ClaudeCodeSessionTests.cs` | 2991 | 11 |
| `src/CodeAlta.Tests/AgentToolsTests.cs` | 2640 | 14 |
| `src/CodeAlta.Plugin.Statistics/Journal/JournalPayloadParser.cs` | 2579 | 4 |
| `src/CodeAlta.Tui/Views/ModelProvidersDialog.cs` | 2546 | 2 |
| `src/CodeAlta.Catalog/SessionJournalSqliteCache.cs` | 2528 | 6 |

## Tests that wait, skip or retry

| file | sleep-like | skips | retry/flaky |
| --- | --- | --- | --- |
| `src/CodeAlta.Browser.Tests/BrowserAnnotationsTests.cs` | 25 | 0 | 0 |
| `src/CodeAlta.Browser.Tests/BrowserDrawingTests.cs` | 21 | 0 | 0 |
| `src/CodeAlta.Browser.Tests/BrowserPluginTests.cs` | 16 | 0 | 0 |
| `src/CodeAlta.Tests/AltaLiveToolTests.cs` | 14 | 0 | 0 |
| `src/CodeAlta.CodeMode.Tests/CodeModeSleepTests.cs` | 12 | 0 | 0 |
| `src/CodeAlta.Desktop.Tests/FleetNodeBudgetQueueTests.cs` | 12 | 0 | 0 |
| `src/CodeAlta/frontend/src/speech/speech.browser.test.ts` | 12 | 0 | 0 |
| `src/CodeAlta.Tests/CodeModeToolTests.cs` | 6 | 0 | 14 |
| `src/CodeAlta.Orchestration.Tests/CodeModeRunStoreTests.cs` | 7 | 0 | 9 |
| `src/CodeAlta.Tests/ClaudeCodeSessionTests.cs` | 10 | 0 | 0 |
| `src/CodeAlta/frontend/src/sessionRetention.browser.test.ts` | 10 | 0 | 0 |
| `src/CodeAlta.Fleet.Update.Tests/UpdateSwitchTests.cs` | 9 | 0 | 0 |
| `src/CodeAlta/frontend/src/markdownContent.browser.test.ts` | 9 | 0 | 0 |
| `src/CodeAlta.Desktop.Tests/CanvasRpcEndpointTests.cs` | 7 | 0 | 0 |
| `src/CodeAlta.Orchestration.Tests/OwnedSessionCommandServiceTests.cs` | 7 | 0 | 0 |
| `src/CodeAlta.Orchestration.Tests/SessionWorktreeTests.cs` | 7 | 0 | 0 |
| `src/CodeAlta.Tests/AcpTurnExecutorTests.cs` | 7 | 0 | 0 |
| `src/CodeAlta/frontend/src/canvases/canvasPanel.browser.test.ts` | 7 | 0 | 0 |
| `src/CodeAlta/frontend/src/composer.browser.test.ts` | 7 | 0 | 0 |
| `src/CodeAlta/frontend/src/documentation/documentationPanel.browser.test.ts` | 7 | 0 | 0 |

## Commits about unstable tests

| commit | date | subject | tests |
| --- | --- | --- | --- |
| 367796271 | 2026-11-10 | Fix the CLI job timeout test on a slow machine | `src/CodeAlta.Cli.Tests/CliRunTests.cs` |
| 9d381259e | 2026-11-04 | Add autonomy to the runs of automations: waiting, timeouts, take-over, dry runs | `src/CodeAlta.Desktop.Tests/AutomationHostTests.cs`, `src/CodeAlta.Desktop.Tests/AutomationRunGuardTests.cs` |

## Largest documents

| file | words | commits | last change |
| --- | --- | --- | --- |
| `src/CodeAlta.Catalog/BuiltinSkills/codealta-plugin-runtime/SKILL.md` | 8958 | 19 | 2026-11-20 |
| `doc/site-screenshots.md` | 8228 | 28 | 2026-11-21 |
| `doc/runtime.md` | 6298 | 24 | 2026-11-14 |
| `site/docs/fleet/machines.md` | 6248 | 27 | 2026-11-16 |
| `site/docs/workspace/prompt.md` | 5857 | 45 | 2026-11-21 |
| `doc/fleet/security-and-privacy.md` | 5854 | 30 | 2026-11-16 |
| `site/docs/plugins/developers.md` | 5621 | 20 | 2026-11-20 |
| `doc/desktop.md` | 5089 | 15 | 2026-11-20 |
| `doc/plugins.md` | 5071 | 19 | 2026-11-20 |
| `doc/fleet/node.md` | 5066 | 34 | 2026-11-16 |
| `site/docs/plugins/mobile.md` | 5051 | 36 | 2026-11-19 |
| `site/docs/fleet/server.md` | 4795 | 17 | 2026-11-13 |
| `site/docs/workspace/settings.md` | 4541 | 28 | 2026-11-22 |
| `site/docs/sessions/code-mode.md` | 4524 | 41 | 2026-11-22 |
| `site/docs/model-providers/setup.md` | 4478 | 20 | 2026-11-19 |
| `site/docs/spaces.md` | 4361 | 14 | 2026-11-20 |
| `site/docs/workspace/readme.md` | 4208 | 21 | 2026-11-21 |
| `doc/fleet/protocol.md` | 4126 | 30 | 2026-11-16 |
| `doc/fleet/deployment.md` | 4091 | 26 | 2026-11-16 |
| `site/docs/troubleshooting.md` | 3968 | 19 | 2026-11-16 |

## Documents changed most

| file | commits | words | last change |
| --- | --- | --- | --- |
| `readme.md` | 47 | 2873 | 2026-11-22 |
| `site/docs/workspace/prompt.md` | 45 | 5857 | 2026-11-21 |
| `site/docs/sessions/code-mode.md` | 41 | 4524 | 2026-11-22 |
| `site/docs/plugins/mobile.md` | 36 | 5051 | 2026-11-19 |
| `doc/fleet/node.md` | 34 | 5066 | 2026-11-16 |
| `site/docs/sessions/sandbox.md` | 33 | 2915 | 2026-11-22 |
| `doc/fleet/security-and-privacy.md` | 30 | 5854 | 2026-11-16 |
| `doc/fleet/protocol.md` | 30 | 4126 | 2026-11-16 |
| `site/docs/automations.md` | 29 | 3429 | 2026-11-20 |
| `doc/site-screenshots.md` | 28 | 8228 | 2026-11-21 |
| `site/docs/workspace/settings.md` | 28 | 4541 | 2026-11-22 |
| `site/docs/fleet/machines.md` | 27 | 6248 | 2026-11-16 |
| `doc/fleet/deployment.md` | 26 | 4091 | 2026-11-16 |
| `doc/fleet/identity.md` | 26 | 3462 | 2026-11-05 |
| `site/docs/command-line.md` | 25 | 2515 | 2026-11-16 |
| `doc/runtime.md` | 24 | 6298 | 2026-11-14 |
| `site/docs/fleet/people-and-access.md` | 24 | 1933 | 2026-11-07 |
| `site/docs/workspace/readme.md` | 21 | 4208 | 2026-11-21 |
| `site/docs/model-providers/readme.md` | 21 | 3672 | 2026-11-21 |
| `site/docs/secrets/readme.md` | 21 | 2282 | 2026-11-20 |
