---
title: "Verify the sandbox on macOS and on a Windows build .9550 or later"
kind: gap
status: pending
created: 2026-10-25
summary: "The sandbox spike ran on Windows 26200.9457 and in WSL Ubuntu only: Seatbelt on macOS and loopback inside a proxy-mode container on newer Windows builds are unverified."
---

## Why

The spike of `.alta/plans/2026-10-25-sandboxing.md` (step 1, findings in the plan) ran on this Windows 11 25H2 computer (build 26200.9457) and in WSL Ubuntu 24.04:

- In proxy mode (the `hosts` network of a profile), a process inside a ProcessContainer cannot listen and connect on loopback: `SocketException: An attempt was made to access a socket in a way forbidden by its access permissions`. vstest-based `dotnet test` then fails ("vstest.console process failed to connect to testhost"). mxc documents listeners inside the container as needing build .9550; it could not be checked here.
- macOS (Seatbelt backend, macOS 15+, Apple silicon) was not available.

## What to do

- On a Windows 11 build 26100/26200.9550 or later: run `tmp/sandbox-spike/Spike` (or the escape and transparency suites of the plan) with the `hosts` network and check that `dotnet test` and a loopback listener work.
- On a Mac with Apple silicon: run the basics (read of `~/.ssh` and `~/.alta` denied, project writable, git, dotnet, network off/on, loopback inside) with the Seatbelt backend, and the escape suite once the worker exists.
- Record the results in the user guide's platform table (`site/docs/sessions/sandbox.md`).

## Where

`src/CodeAlta.Sandbox` (backend and availability), the container tests, `site/docs/sessions/sandbox.md`.

## macOS: done (2026-10-26)

Verified on macOS 27.0.1 (Apple silicon) with the Seatbelt backend; the escape suite now runs on macOS and Linux (`CliSandboxEscapeTests`), with a test of the developer tools in a container (`CliSandboxWorkerTests`). What it found was fixed in the same change, and the user guide's platform list says what remains (`-m:1`, no `dotnet test`). See "How it is verified" in the plan. The Windows build .9550 part stays open.
