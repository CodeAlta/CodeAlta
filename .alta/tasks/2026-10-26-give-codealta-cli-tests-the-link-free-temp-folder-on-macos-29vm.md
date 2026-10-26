---
title: "Give CodeAlta.Cli.Tests the link-free temp folder on macOS"
kind: problem
status: pending
created: 2026-10-26
summary: "Alta_LeavesTheSpacesProjectsAndGlobalPromptsOfTheProfileAsTheyAre fails on macOS on a clean main: its temporary root is under /var, a link, and prompt management refuses linked paths."
---

## Why

On macOS (2026-10-26, clean worktree of `b8dc1afb`), `CodeAlta.Cli.Tests` fails one test every run:

```text
Failed Alta_LeavesTheSpacesProjectsAndGlobalPromptsOfTheProfileAsTheyAre
{"type":"alta.error",...,"code":"prompt.storageFailed","commandPath":"alta prompt create","message":"Prompt management does not edit linked paths."}
```

`PromptResourceStore.CheckedPath` (`src/CodeAlta.Catalog/PromptResourceStore.cs:289`) refuses any linked component, and the test root is under `/var/folders/…`, where `/var` links to `/private/var`. The other test projects whose fixtures need a root without links compile `src/CodeAlta.Tests/LinkFreeTempDirectory.cs` (Catalog, Orchestration, Desktop, Sandbox, Plugin.Statistics); `CodeAlta.Cli.Tests` does not.

## What to do

Add `<Compile Include="..\CodeAlta.Tests\LinkFreeTempDirectory.cs" Link="LinkFreeTempDirectory.cs" />` to `src/CodeAlta.Cli.Tests/CodeAlta.Cli.Tests.csproj`, and check that the test passes on macOS and still on Windows and Linux.

## Where

`src/CodeAlta.Cli.Tests/CodeAlta.Cli.Tests.csproj`, `src/CodeAlta.Cli.Tests/CliHostCommandsTests.cs:44`.
