# SWE-bench Pro v2 HARD-51: reproduction kit

Runs the 51 tasks of HARD-51 of [SWE-bench Pro v2](https://github.com/scaleapi/SWE-bench_Pro-os/blob/main/v2/README.md) with [Harbor](https://github.com/laude-institute/harbor), under its locked protocol, for four harness/model pairs, and optionally a fifth, one job at a time:

| Run (`-Runs` name) | Harness | Model |
|---|---|---|
| `codealta-opus` | CodeAlta's own agent loop, `anthropic` provider (Messages API) | Claude Opus 5.5, `xhigh` |
| `claude-code-opus` | Claude Code CLI | Claude Opus 5.5, `xhigh` |
| `codealta-astra` | CodeAlta's own agent loop, `openai-responses` provider | GPT-6 Astra, `xhigh` |
| `codex-astra` | Codex CLI 0.155.1 (code mode) | GPT-6 Astra, `xhigh` |
| `codealta-cc-opus` (optional) | CodeAlta driving the Claude Code CLI 2.1.309, `claude-code` provider | Claude Opus 5.5 (`opus`), `xhigh` |

CodeAlta runs through its adapter, [`../harbor/locked_codealta.py`](../harbor/README.md), which maps `-m anthropic/<model>` to the `anthropic` provider type. CodeAlta / Opus is therefore not CodeAlta driving the Claude Code CLI. The native harnesses use the locked adapters of SWE-bench Pro (`v2/tooling`).

`codealta-cc-opus` is CodeAlta driving the Claude Code CLI (`--ak provider_type=claude-code`): the adapter installs the version of Claude Code that CodeAlta pins during setup, and the CLI makes the model requests with `ANTHROPIC_API_KEY`. Beside `claude-code-opus`, it tells what CodeAlta adds or costs on top of Claude Code's own loop. It is not in the default `-Runs`: name it (`-Runs codealta-cc-opus`, alone or with the others). See [CodeAlta on the Claude Code CLI](../harbor/README.md#codealta-on-the-claude-code-cli).

Each model job is followed by a pristine replay of its captured patches, which gives the authoritative score. The reference run, its results and its analysis are in [`reference-20261026-162310.md`](reference-20261026-162310.md) and [`../2026-10-26-swe-bench-pro-v2-hard51.md`](../2026-10-26-swe-bench-pro-v2-hard51.md).

Results vary between attempts: compare runs, not single numbers.

## Pinned versions

| Component | Version |
|---|---|
| SWE-bench Pro v2 | `66f92766bba642462d4bbe5479e83f91f9211862` |
| Harbor | `0.24.0` |
| Codex CLI (in the locked adapter) | `0.155.1` |
| CodeAlta | this checkout, or the revision given to `setup.ps1` |

The reference run used CodeAlta `2.0.1-alpha.0.113+b8dc1afb` with the dual glibc/musl package of #224 (merged as `a87380ec`). HARD-51 has Alpine images, so a CodeAlta older than #224 cannot run it.

## Machine

- Windows 11 with WSL2 and Docker Desktop on Linux containers.
- 64 GiB of RAM, with 48 GiB and 16 GiB of swap given to WSL, and 32 logical processors.

  Each task asks for 4 GiB, 1 CPU and 10 GiB of disk: 8 sandboxes at a time need 38 GiB of Docker memory. In `%UserProfile%\.wslconfig`:

  ```ini
  [wsl2]
  memory=48GB
  processors=32
  swap=16GB
  ```

  Apply it with `wsl --shutdown` once no WSL work runs, start Docker Desktop, and check with:

  ```powershell
  docker info --format '{{.OSType}} {{.KernelVersion}} {{.NCPU}} CPUs {{.MemTotal}} bytes'
  ```

- A WSL kernel with nftables `CONFIG_NFT_FIB_INET`, for Harbor's locked networking. WSL `3.0.3.0` with kernel `6.18.54.1` has it; kernel `5.15.167.4` does not. `setup.ps1` checks it.
- At least 300 GiB of free disk for the task images.
- PowerShell 7, Git, Python 3.11–3.13 and the .NET 10 SDK.
- **A short path.** Docker Compose on Windows fails with `WinError 267` when the paths of the sandboxes get too long, and the scripts refuse a kit path over 80 characters. `C:\code\CodeAlta\benchmarks\swe-bench-pro` is fine. For a deeper checkout, copy this folder to a path such as `C:\sbp-v2` and pass `-CodeAltaRoot` to `setup.ps1`.

## Setup

From this folder:

```powershell
pwsh -File .\scripts\setup.ps1                                  # build this checkout as it is
pwsh -File .\scripts\setup.ps1 -CodeAltaRevision <commit|branch> # build another revision
```

- **Benchmark:** it clones SWE-bench Pro into `workspace\` with LF line endings, sparse on `v2`, and checks out the pinned commit.
- **Harbor:** it installs Harbor 0.24.0 into `workspace\harbor-venv`, then checks Docker (Linux mode, memory) and the kernel's support for locked networking.
- **Package:** it builds the dual glibc/musl `altacli` package with [`../harbor/build-altacli.ps1`](../harbor/build-altacli.ps1) and checks its checksum.
  - `-CodeAltaRevision` builds from a detached worktree under `workspace\`, so the checkout you work in keeps its branch and changes. A local branch or commit is used as it is; anything else is fetched from `origin`.
  - `-Configuration Debug` builds `altacli` in Debug. Use it when a CodeAlta of this checkout runs from `bin\Release` (see "Debug or Release" in [AGENTS.md](../../AGENTS.md)).
- **Record:** `workspace.json` records the CodeAlta commit, whether its worktree had local changes, and the package's sha256. The run manifest repeats them.

Everything the kit creates is ignored by Git: `workspace\`, `workspace.json`, `datasets\`, `jobs\` and `results\`.

## Model-free preflight

```powershell
pwsh -File .\scripts\run-hard51.ps1 -PrepareOnly
```

It spends nothing:

- It takes the 51 tasks of the official `v2\hard51_ids.txt` and checks them against `v2\SHA256SUMS`, which catches a checkout whose line endings were converted.
- It builds the dataset with the official `make_subset.py` and checks Docker's memory.
- It runs an install-only Harbor job in all 51 images for each harness that will run; `codealta-cc-opus` has its own, which also installs the Claude Code CLI.

## API keys

The runs use API keys, billed per token; subscriptions (ChatGPT, Claude, Copilot) do not apply. Set the keys only in the terminal that starts the run, never in a script, an argument or a file:

```powershell
$anthropic = Read-Host "Anthropic API key" -AsSecureString
$env:ANTHROPIC_API_KEY = [Net.NetworkCredential]::new("", $anthropic).Password
$openai = Read-Host "OpenAI API key" -AsSecureString
$env:OPENAI_API_KEY = [Net.NetworkCredential]::new("", $openai).Password
```

A run that has only Opus jobs needs only `ANTHROPIC_API_KEY`, and one with only Astra jobs only `OPENAI_API_KEY`.

## Paid run

```powershell
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun                                    # all four jobs
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun -Runs codealta-opus,codealta-astra # CodeAlta only
pwsh -File .\scripts\run-hard51.ps1 -ConfirmPaidRun -Runs codealta-cc-opus            # CodeAlta on the Claude Code CLI
```

- **Order:** the jobs run one after the other, in the order of the table above, each followed by its replay. `codealta-cc-opus` needs `ANTHROPIC_API_KEY`.
- **Concurrency:** 8 task sandboxes at a time (`-Concurrency`).
- **Retries:** one retry, for `NetworkConnectionError` only.
- **Stop on error:** a job that has any errored trial stops the run. Harbor can exit with 0 while its trials failed, so the script checks each job's `result.json`.
- **Cost:** the reference run cost about $497 for all four jobs, of which about $279 for the two CodeAlta jobs. It took about 5 h 50 min, preflights and replays included.
- **Progress:** Harbor shows the completed tasks, their phases, the time left and the mean reward so far. Each job's `result.json` under `jobs\<run-id>-<job>\` is updated as it goes, and `.\workspace\harbor-venv\Scripts\harbor.exe view .\jobs --jobs` opens a browser view.
- **Dry run:** `-DryRun` prints the Harbor commands without running anything.

## Summary

```powershell
.\workspace\harbor-venv\Scripts\python.exe .\scripts\summarize-results.py --run-id <run-id>
.\workspace\harbor-venv\Scripts\python.exe .\scripts\summarize-results.py --run-id <run-id> --baseline-run-id <earlier-run-id>
```

It writes `results\<run-id>-summary.md` and `.json`, and covers:

- the replay scores and the required tests;
- the pairwise outcomes and the failed tests (CodeAlta CC / Opus against Claude Code / Opus when it ran);
- times (mean, median, P90, agent-hours);
- tokens: input, cache read, cache write and output;
- costs.

**Costs** are estimated from the tokens at list prices, the same way for every run. Those prices reproduce exactly Claude Code's own `total_cost_usd` and Harbor's Codex `cost_usd`. Harbor's reported cost is shown beside the estimate: for Claude Code it misses the segments of a trial that restarted, and CodeAlta reports one only on the Claude Code CLI (the estimate `altacli` makes for the other providers is in the metadata of each trial, not in Harbor's cost). The provider's billing is the final word.

**Partial runs:** a run made with `-Runs` is summarized with the jobs it has. `--baseline-run-id` takes the missing jobs from an earlier run in the same `jobs\` folder. That compares new CodeAlta jobs with the native jobs of a reference run, but runs made on different days or machines can differ for reasons outside the harness.

## Cleanup

```powershell
pwsh -File .\scripts\cleanup.ps1                        # jobs, datasets and summaries
pwsh -File .\scripts\cleanup.ps1 -RemoveWorkspace       # also the benchmark checkout, Harbor and the worktrees
pwsh -File .\scripts\cleanup.ps1 -RemoveSweBenchImages  # also the SWE-bench v2 images (downloaded again next time)
```

## What each trial writes

| Job | Files |
|---|---|
| CodeAlta model job | `result.json`; `agent\altacli.jsonl` and `agent\altacli.stderr`; `agent\model.patch`; `verifier\output.json`; `verifier\run-script-stdout.txt` and `-stderr.txt` |
| Native model job | the same, with `agent\trajectory.json` and the harness's own trace in place of `altacli.jsonl` |
| Replay job | `result.json`; `agent\replay.json`; `verifier\output.json` |

The adapter's own options and outputs are described in [`../harbor/README.md`](../harbor/README.md).
