---
name: ultra-profiler
description: Use this skill to profile the CPU of a .NET application, a .NET application with native code, or a native application with the Ultra profiler, run through `dnx` - find why a program is slow or uses the CPU, capture a profile of a running process or of a program from its start, then read its hot functions, call trees, GC and JIT events.
---

# Profile an application with Ultra

[Ultra](https://github.com/xoofx/ultra) is a sampling CPU profiler: managed, native and kernel stacks, GC and JIT events, on Windows (ETW), Linux (perf) and macOS ARM64. It runs with `dnx`, the tool runner of the .NET SDK: the first run downloads Ultra into the NuGet cache, the next ones start at once. Nothing is installed.

```sh
dnx -- ultra <command> [options]
```

The first `--` ends the options of `dnx`: what follows `ultra` goes to Ultra unchanged, `--help` and Ultra's own `--` too (`dnx -- ultra profile ... -- <program> <arguments>` launches a program to profile).

## Start with its guide

Ultra documents itself for agents. Read its guide before the first capture, and follow it:

```sh
dnx -- ultra guide
```

It gives the capture options, the result file, the report and the tools of its MCP server. Where it writes `ultra <command>`, run `dnx -- ultra <command>`. `dnx -- ultra <command> --help` lists every option of a command.

## In CodeAlta

- **Where the profiles go.** Write them, and the `--result-file`, in a temporary folder or a folder the user names, with `--output`: not in the project, whose files the user did not ask to change. A profile is a `.json.gz` of tens of MB.
- **Elevation on Windows.** A capture needs administrator rights: `--elevate` shows a UAC prompt to the user. Tell the user to expect it before you start the capture. A declined prompt fails the capture: say so, do not retry in a loop.
- **A capture that takes long** runs as a background job: `alta job start --command "dnx -- ultra profile --elevate --duration 30 --result-file <folder>/result.json --stop-file <folder>/stop.txt --output <folder>/<name> --pid <pid>" --title "Profile <name>"`. Do the slow operation meanwhile, create the stop file to end the capture early, and read the result file once the job reports its end. A short capture (a few seconds) can run in the foreground.
- **The analysis.** Start with `dnx -- ultra report <profile.json.gz>`. For call trees, callers, hotspots by name, time windows or GC markers, use the tools of `ultra mcp`:
  - `alta mcp list` shows whether a server runs `ultra mcp`. If none does, ask the user before adding one, then `alta mcp server add ultra --command=dotnet --arg=dnx --arg=ultra --arg=mcp --scope=global` (`dotnet dnx`, since `dnx` is a `.cmd` script on Windows).
  - `alta mcp activate ultra` registers its tools in the running turn. Its own instructions and `get_llm_guide` say how to use them.
- **The report to the user**: the hot paths with their share of the CPU, what they mean in the code, and the path of the profile. A human opens the profile at https://profiler.firefox.com/.

## When it does not run

- `dnx` not found, or `dotnet: command not found`: `dnx` needs the .NET 10 SDK or later. Tell the user; do not install an SDK without asking.
- The download fails: the machine may be offline or have no access to nuget.org. Tell the user what failed.
- On Linux, a capture needs root or `kernel.perf_event_paranoid <= 0`; on macOS, Ultra launches the program itself and cannot attach to a pid. Ultra's guide says what each system allows.
