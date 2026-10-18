---
title: "Fix the flaky paused search assertion of DesktopSearchMeaningTests"
kind: problem
status: pending
created: 2026-10-18
summary: "TurnedOn_ItEmbedsTheIndex_AnswersByMeaning_PausesAndTurnsOff fails about 2 runs in 6 on macOS at line 92: the meaning search while paused does not always return doc:b.md first."
---

## Why
Run alone 6 times on macOS in Debug (`dotnet test -c Debug --no-build --filter "FullyQualifiedName~TurnedOn_ItEmbedsTheIndex"` from `src/CodeAlta.Desktop.Tests`), it failed twice with "Expected strings to be equal. Strings have different lengths (expected: 8, actual: 10)" at `DesktopSearchMeaningTests.cs:92`. That line expects `doc:b.md` and gets another ref, probably a `doc:nN.md` note. The test touches no skill code: seen during the skill catalog cache work (#175) on a base that already failed this way.

## What to do
Find why the first hit of the paused meaning query changes from run to run: the order of the fake embeddings or ties in scoring with the 40 notes added just before, or a race with the pass that the pause interrupts. Then make the assertion deterministic without weakening what it checks (queries are still answered while paused).

## Where
`src/CodeAlta.Desktop.Tests/DesktopSearchMeaningTests.cs` (lines 71-92), `DesktopSearchMeaning` and the fake engines of that test.
