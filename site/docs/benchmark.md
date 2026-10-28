---
title: Harness Benchmark
---

# Harness Benchmark

A coding agent is a model and a harness: the loop that sends the model its instructions, runs its tools, edits the files and keeps the context. The model is the same wherever you use it; the harness makes the difference in what it gets done and what it costs.

We measure CodeAlta's harness against the harnesses the model vendors make for their own models: **Claude Code** for Claude Opus, **Codex** for GPT. Same models, same tasks, same machine: only the harness changes.

## Results

51 hard tasks of [SWE-bench Pro](https://github.com/scaleapi/SWE-bench_Pro-os), real issues of 11 open-source projects, each graded by its project's own tests.

| Claude Opus 5.5 | CodeAlta | Claude Code |
|---|---:|---:|
| Tasks resolved | **50 / 51** | **50 / 51** |
| Required tests passed | 3,848 / 3,852 | 3,848 / 3,852 |
| Cost of the 51 tasks | **$76** | $87 |
| Input tokens | **97.0M** | 117.9M |
| Output tokens | **1.76M** | 1.90M |
| Time per task (median) | **8.6 min** | 9.3 min |

| GPT-6 Astra | CodeAlta | Codex |
|---|---:|---:|
| Tasks resolved | **47 / 51** | 46 / 51 |
| Required tests passed | **3,845** / 3,852 | 3,813 / 3,852 |
| Cost of the 51 tasks | $136 | **$131** |
| Input tokens | 51.7M | **50.7M** |
| Output tokens | 0.92M | **0.76M** |
| Time per task (median) | 6.7 min | **5.1 min** |

- **With Claude Opus**, CodeAlta solves the same 50 tasks as Claude Code, for 12% less: it sends the model 18% fewer tokens, and a task takes a little less time.
- **With GPT-6 Astra**, CodeAlta solves one task more than Codex and passes more of the required tests, at about the same cost (4% more).
- Costs are computed from the tokens each harness used, at the providers' list prices, the same way for every run. On the 51 tasks, the cache served 92 to 95% of the input of every harness.

## What it means for you

- **Results on par with the vendors' own agents.** On hard tasks of real projects, CodeAlta resolves as many tasks as Claude Code with Claude, and one more than Codex with GPT: you do not trade quality for an open, provider-agnostic harness.
- **One harness for both families.** The same CodeAlta agent matches the best score of each family, with Anthropic and with OpenAI models. Changing provider or model does not mean changing tools, habits or sessions.
- **Costs in line with the native agents.** 12% less than Claude Code with Claude, 4% more than Codex with GPT. Batching reads and edits in [Code Mode](sessions/code-mode.md) is part of why.
- **The harness you use.** The benchmark runs [CodeAlta CLI](command-line.md), which runs the agents of CodeAlta Desktop and CodeAlta TUI without a window: the same agent loop and tools, with Code Mode on as by default.

## How it was run

- **Benchmark:** SWE-bench Pro v2, the HARD-51 set: 51 tasks among the hardest of the benchmark, in Python, Go, TypeScript and JavaScript projects (Ansible, Teleport, Element, Flipt, Open Library, Qutebrowser, Navidrome, Proton Mail and others).
- **Protocol:** the locked protocol of SWE-bench Pro, run with [Harbor](https://github.com/laude-institute/harbor). Each task runs in a sandbox of its own whose network reaches only the model's endpoint. A task is resolved when all its required tests pass: after each run, the patch of every task is applied to a clean copy of its project and graded again.
- **Models:** Claude Opus 5.5 and GPT-6 Astra, both at the `xhigh` reasoning effort, through the Anthropic and OpenAI APIs.
- **Harnesses:** CodeAlta with Code Mode on, one agent per task (no sub-agents, so that every request goes to the model being measured); Claude Code 2.1.310 and Codex 0.155.1 with their own settings.
- **Machine:** one Windows workstation, Docker on WSL 2, 8 tasks at a time.
- **When:** in the same week of October 2026, one attempt per harness and model. Between attempts of the same harness, the score can vary by one or two tasks: compare the trend, not a single task.

The kit that reproduces these runs, its scripts and the detailed report are in the [CodeAlta repository](https://github.com/CodeAlta/CodeAlta/tree/main/benchmarks).
