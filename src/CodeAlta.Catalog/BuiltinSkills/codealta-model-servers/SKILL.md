---
name: codealta-model-servers
description: Use this skill to run local models with llama.cpp on this computer or on the machines of the fleet - install or update the best build of llama.cpp for their GPUs (CUDA, ROCm, Vulkan, Metal), choose a GGUF model of Hugging Face and a quantization that fits, download it, add, start, test or change a llama-server (several models on demand, or one always loaded), make the sessions use it as a provider, plan updates, and find why a server or a model fails.
---

# Model servers (llama.cpp)

CodeAlta runs llama.cpp servers on this computer and on the machines it manages, and keeps their builds and models up to date. Use the `alta` tool: each command answers with JSON records, one per line. `alta llama --help` lists the commands, and `alta llama <command> --help` gives the options of each.

A machine is `local` (this computer) or a paired machine, by its name; `--machine` defaults to this computer. A model is `publisher/repository:QUANT`, as on Hugging Face (`unsloth/Qwen3-8B-GGUF:Q4_K_M`).

## See what a machine runs

- `alta llama machine list` lists the machines, whether each is online and whether this computer manages it (`manages`): only a managed machine is changed.
- `alta llama status --machine <machine>` says its GPUs and their memory, its builds of llama.cpp (`builds`, `latest`, `updateAvailable`, `recommended`), its update policy, its servers, models, downloads, tasks and warnings.
- `alta llama build list --machine <machine>` says, for each backend, whether it fits the GPUs, drivers and system (`verdict`, `message`).

## Install llama.cpp

`alta llama build install --machine <machine>` installs the recommended build; `--backend rocm` (or `cuda`, `vulkan`, `metal`, `cpu`) chooses one. Each build is checked against its digest and tested on the GPUs before use. It runs on the machine: its end is sent to you as a prompt, so do not poll it.

## Choose a model that fits

1. `alta llama hub search <words> --author unsloth` finds GGUF models (the trending ones first; `--sort downloads`).
2. `alta llama hub show <publisher/repository>` gives each quantization with its size and, for each managed machine, its `fit`: `gpu` (on one GPU), `gpus` (split across them), `partly` (in part in the memory of the system: slow) or `too_large`, and `context`, the largest context that fits, from the header of the model (`shape` gives its KV cache per 1,000 tokens). Prefer the largest quantization that fits on the GPUs with the context the work needs (32k tokens or more for an agent), and Q4_K_M or better for chats.
3. `alta llama model download <publisher/repository:QUANT> --machine <machine>` downloads it into the Hugging Face cache of the machine. Its end is sent to you as a prompt.

`alta llama model list --machine <machine>` lists the models on its disk, the servers that use each and what removing it frees. After a download, the machine measures how the model fits its GPUs with llama-fit-params (`measured`: the context that fits and what each GPU takes): trust it over the estimate.

## Serve a model and use it in sessions

- `alta llama server add --model <publisher/repository:QUANT> --name <name> --machine <machine> --use` adds a server and starts it: a missing model is downloaded first, the build installed first, and the server starts once they are there. `--use` adds a provider so that the sessions of this computer use it.
- A server of `--kind several` (the default) loads each model at its first request and frees the GPU of the one used least when it needs room; give several `--model`. `--kind one` keeps one model loaded: the fastest answers, its GPU always taken; `--purpose embedding` or `rerank` serves embeddings or reranking.
- `--context <tokens>` sets the context (by default, what fits the GPUs), `--parallel <n>` the requests at once, `--gpu <device>` the GPUs (`CUDA0`, `ROCm1`), `--arg <option>` an option of llama-server.
- `alta llama server use <id> --machine <machine>` adds the provider later; `alta model list` then lists its models.
- A llama-server the user runs by hand: `alta llama server import --machine <machine>` reads its command line on stdin into a definition, and `alta llama server add --stdin` saves that definition.

## Run, test, change

- `alta llama server list --machine <machine>` lists the servers, their state (`ready`, `starting`, `waiting` for a build or a model, `failed`) and the state of each model.
- `alta llama server test <id>` asks a model for a short answer and gives its speed.
- `alta llama server start <id>`, `alta llama server stop <id>`, `alta llama server restart <id>`; `alta llama server load <id> <model>` and `alta llama server unload <id> <model>` for a server of several models.
- `alta llama server show <id>` gives its whole definition; `alta llama server set <id> --context 65536` changes one setting, `alta llama server set <id> --stdin` replaces it with an edited definition.
- When a server or a model fails, read `alta llama server log <id>` before changing anything: a model too large for the memory of the GPU wants a smaller quantization, a smaller `--context`, or another GPU.

## Updates

- `alta llama update now --machine <machine>` updates its builds now: it waits for its servers to be idle, tests each with the new build, and goes back to the previous one when a test fails. Its end is sent to you as a prompt.
- `alta llama update at <time> --machine <machine>` plans one update; the machine keeps it, even while this computer is off. `alta llama update cancel` cancels it.
- `--all` instead of `--machine` updates or plans every machine this computer manages and reaches; `--build <bNNNN>` takes them all to one build (an older one takes them back). `update now --all` updates them at once: to update them one after the other, update one and the next when its end comes.
- `alta llama update policy` says the policy; with `--schedule daily --time 04:00` (or `weekly --day sunday`), `--channel stable|every|pinned` and `--models on`, it changes it. Change it only when the user asks. Where the official ROCm build cannot run (no HIP SDK, an older Linux), the user may allow Lemonade's ROCm builds in Settings > Fleet > Model servers > Policy; their builds are named `lemonade-bNNNN`.
- `alta llama build use --backend <backend> --build <bNNNN>` goes back to an earlier build.

## What you may do

- Remove a model (`alta llama model remove`) or a server (`alta llama server remove`), or change the update policy, only when the user asks.
- A session whose commands the user reviews, or that does without the shell, cannot install, update, start or test: it reads, and tells the user what to do in Settings > Fleet > Model servers. A sandboxed session reads only.
- A machine this computer uses but does not manage is changed by the computers that manage it.
- Downloads, installs and updates tell you their end: end your turn or do other work meanwhile, never a loop of `alta llama status`.
