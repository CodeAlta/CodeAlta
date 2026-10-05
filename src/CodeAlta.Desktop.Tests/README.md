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

The packaged desktop is not qualified by a test here. The former opt-in `DesktopNative` test and its
`Verify-DesktopPackage.ps1` driver expected the single-package layout of 1.0.0 and were removed when
the tool moved to one package per runtime. The isolated [M0 native smoke fixture](NativeSmoke/README.md)
keeps its own scripts; it is outside the release solution, so its source and assets cannot enter the
production package.

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
