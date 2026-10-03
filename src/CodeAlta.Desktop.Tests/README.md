# Desktop tests

`CodeAlta.Desktop.Tests` is an MSTest project in the normal solution. Managed tests cover early
help/version/argument admission without native initialization, the boot RPC's generated JSON,
controlled navigation, assembly identity and dependency direction. The first workspace vertical
also covers actual CLI admission and durable-catalog projection seams using inert inputs. It does
not construct real catalogs, SQLite, providers or a native application in behavioral fixtures.
Tests here are behavioral or inspect compiled metadata; source-text pinning checks (fragment,
regex or whole-file hash assertions over repository files) were removed.

For current scoped qualification, use only independently audited exact method filters with
`--no-build --no-restore`; the historical broad commands below are not isolation authorization.
The exact vertical inventory and remaining gaps are recorded in
[the parity ledger](../../doc/dual-head-desktop-parity.md). Native scripts remain separate.

```powershell
dotnet test src/CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj -c Release
npm --prefix src/CodeAlta/frontend test
dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureGuardrailTests|FullyQualifiedName~TuiIdentityTests"
```

The `DesktopNative` category is **opt-in**. Without the opt-in flag, or without supported graphical
infrastructure, it reports inconclusive/skipped with the reason, not a fake native pass. The automated
driver currently requires interactive Windows x64 + installed WebView2, SDK 10 and build-time npm/Node.
A requested run with broken native initialization/build/assertions fails; it is not converted to a skip.

```powershell
$env:CODEALTA_DESKTOP_NATIVE_TESTS = '1'
dotnet test src/CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj -c Release --filter TestCategory=DesktopNative
# Equivalent directly invoked driver:
./src/CodeAlta.Desktop.Tests/Verify-DesktopPackage.ps1
```

`Verify-DesktopPackage.ps1` qualifies the actual packaged desktop boot/CLI/close, then builds/runs
the isolated [M0 native smoke fixture](NativeSmoke/README.md), preserving generated RPC/channel
cancellation, lazy local assets/CM/Radix/Mermaid, dialogs and canceled/approved native close checks.
The fixture is outside the release solution; its source/assets cannot enter the production package.
The pinned npm graph is preserved for both consumers; the production entry imports React and
its boot/workspace/history contracts, not the smoke-only visual libraries.

The bounded-history slice was verified with 19 individually audited C# methods (10 inert
stream/containment, five RPC projection/callback, four named-source checks), four existing
workspace source regressions and eight tests in `frontend/src/history.test.ts`. Contract
generation/checking, TypeScript checking, cached Vite production build and targeted/solution
Release builds passed without dependency fetching. These tests do not construct stores,
catalogs, SQLite, runtime or native hosts. They do not qualify actual copied-cache access,
renderer mounting/visual interaction, transactional revisions or shutdown joining. Source
checks preserve six entire originals and the preceding workspace inverse chain; frontend
helper tests plus source wiring are not DOM/React lifecycle tests. The named-source checks and
inverse chains recorded in this paragraph have since been removed.

These scripts retain their unique evidence roots, local package feed/tool installs and native browser
cache. They do not clean user profiles or ignored original prototype artifacts. Never publish the
smoke package. [Qualification boundaries and historical evidence](../../doc/desktop-native-qualification.md).
