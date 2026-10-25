# CodeAlta on SWE-bench Pro v2 (Harbor)

`locked_codealta.py` is a [Harbor](https://github.com/laude-institute/harbor) agent, `codealta-locked`, that runs the CodeAlta harness (`altacli run`) on [SWE-bench Pro v2](https://github.com/scaleapi/SWE-bench_Pro-os/blob/main/v2/README.md) under its locked protocol, like the `locked_codex.py` and `locked_claude_code.py` agents of `v2/tooling`.

## Build

The sandbox images have no .NET: publish a self-contained `linux-x64` `altacli` and pack it.

```powershell
benchmarks/harbor/build-altacli.ps1        # or: sh benchmarks/harbor/build-altacli.sh
```

This writes `benchmarks/harbor/dist/altacli-linux-x64.tar.gz` (about 55 MB) and its `.sha256`. The publish sets `InvariantGlobalization`, because some images lack libicu. It also replaces the native SQLite of the package (SQLitePCLRaw 2.1.13, which needs glibc 2.34) with the one of SQLitePCLRaw 2.1.11 (SQLite 3.49.1, glibc 2.28), downloaded from nuget.org and checked by its sha256: many task images are Debian 11 (glibc 2.31). The adapter uploads the tarball by default; `--ak altacli_tarball=<path>` uses another one, and `--ak altacli_url=<url> --ak altacli_sha256=<sha>` downloads a pinned tarball in the sandbox, with the upload as the fallback.

## Run

Harbor 0.24 or later, Docker with Linux images, and a clone of `SWE-bench_Pro-os` (for `v2/tasks` and `v2/tooling`). On Windows, clone it with `git -c core.autocrlf=false clone ...`: with CRLF line endings the test patches no longer apply and every task fails, the reference solution too (`cd v2 && sha256sum -c SHA256SUMS` checks it). Put both tooling folders on `PYTHONPATH` (`;` instead of `:` on Windows):

```sh
export PYTHONPATH=v2/tooling:<codealta>/benchmarks/harbor
export ANTHROPIC_API_KEY=...

harbor run -p v2/tasks -e docker -n 4 \
  -a locked_codealta:LockedCodeAlta -m anthropic/claude-opus-5-5 --ak reasoning_effort=xhigh \
  --allow-agent-host api.anthropic.com --job-name sbp-v2-codealta-opus

# authoritative score: re-grade every captured diff on a pristine image
harbor run -p v2/tasks -e docker -n 4 \
  -a patch_replay:PatchReplayAgent -m replay --ak source_job=jobs/sbp-v2-codealta-opus \
  --job-name sbp-v2-codealta-opus-regrade
```

`-e modal` works the same way. `-i <instance_id>` runs one task; `$(sed 's/^/-i /' v2/hard51_ids.txt)` runs HARD-51.

The agent phase is offline: the tasks set `[agent] network_mode = "no-network"`, and `--allow-agent-host` lets only the model endpoint through. Harbor's `docker` environment enforces it with its egress-control sidecar (Linux containers), with Docker Desktop on Windows too; the setup and verifier phases keep their network. `v2/tooling/probe/make_probe_task.py --kind net` checks it: put `LLM_ENDPOINT` in the probe task's `[solution.env]`, since `-a oracle` reads that one.

### Options (`--ak key=value`)

| Option | Default | Use |
| --- | --- | --- |
| `reasoning_effort` | the model's | `none` ... `xhigh`, `max`: `-m provider:model@effort` |
| `timeout_sec` | `3000` | The agent budget; `altacli --timeout` gets budget − 90 s, the in-sandbox `timeout` budget − 60 s |
| `provider_type`, `api_key_env`, `api_url` | from `-m` | Another provider or endpoint (below) |
| `prompt_id` | `default` | The CodeAlta agent prompt |
| `disable_tools` | `web` | `--disable-tools` patterns |
| `plugins` | `false` | `true` starts plugins and MCP servers |
| `altacli_tarball`, `altacli_url`, `altacli_sha256` | `dist/...` | Where `altacli` comes from |
| `extra_args` | | More `altacli run` arguments, shell-quoted |

## Model mapping

Harbor's `-m <provider>/<model>` becomes a CodeAlta provider of a fresh profile, set with `-c` (no `config.toml` is read), and `altacli run -m <provider>:<model>[@effort]`:

| `-m` provider | CodeAlta `type` | Key variable | `--allow-agent-host` |
| --- | --- | --- | --- |
| `anthropic` | `anthropic` | `ANTHROPIC_API_KEY` | `api.anthropic.com` |
| `openai` | `openai-responses` | `OPENAI_API_KEY` | `api.openai.com` |
| `google`, `gemini` | `google-genai` | `GEMINI_API_KEY`, else `GOOGLE_API_KEY` | `generativelanguage.googleapis.com` |
| other | `--ak provider_type=` (`openai-chat`, `mistral`, `xai`...) | `--ak api_key_env=` | the host of `--ak api_url=` |

For `-m gemini/gemini-3.8-flash` the adapter runs `-c providers.gemini.type="google-genai" -c providers.gemini.api_key_env="GEMINI_API_KEY" -c chat.default_provider="gemini"` and `-m gemini:models/gemini-3.8-flash` (the Gemini API names its models `models/<id>`). The key is read from the host environment or `--ae`, and passed to the `altacli` process only.

## What it runs and writes

In the sandbox, from the repository (`/app`, or `/testbed`):

```sh
timeout -k 30 <budget-60> /opt/codealta/altacli run --ephemeral --profile /tmp/codealta-profile --ignore-user-config \
  -C /app -m <provider:model[@effort]> --offline --format jsonl --timeout <budget-90> \
  --disable-tools web --no-plugins --no-mcp -c ... - < /tmp/codealta-run/instruction.md
```

- `--ephemeral --profile`: an empty profile (no settings, credentials, skills of a user) and a state root deleted at the end.
- `--no-plugins --no-mcp`: the plugins `altacli` can run are Git (the `gh`, `glab` and `az` CLIs), Jira, MCP and ACP. None of them can work offline, and an empty profile configures no MCP server: leaving them out keeps the harness the same from task to task.
- `--offline`: no models.dev refresh, which the sandbox could not reach.

Under `jobs/<job>/<trial>/agent/`:

| File | Content |
| --- | --- |
| `altacli.jsonl` | The trace: `_codealta/session`, the ACP `session/update` lines (messages, thinking, tool calls), `_codealta/result` last |
| `altacli.stderr` | Errors and the log of `altacli` |
| `model.patch` | `git diff` of the repository against the task's base commit, always written, also after a timeout or a failure; without `.alta/` |
| `agent-commits.txt` | The commits the agent made, when it made some |

CodeAlta's agents may commit their work, on a branch of their own. Before the capture, the adapter moves `HEAD` back to the base commit with `git reset --soft`: the work stays in the files, and the verifier, which restores the test files from `HEAD`, grades the same change in the sandbox as `patch_replay` does on a pristine image.

`result.json` gets `n_input_tokens` (cached included), `n_cache_tokens`, `n_output_tokens` and `cost_usd` from the `_codealta/result` line; a value it does not give stays unset, never zero. Its `agent_result.metadata` holds the status, exit code, stop reason, failure and duration of the run.

## Limits

- Cost: providers that report no cost (Gemini, and others through `api_key_env`) leave `cost_usd` unset; compute it from the tokens.
- A run killed by the in-sandbox `timeout` (it did not stop at `--timeout`) has no result line: no tokens, no cost.
- The session journal is not kept (`--ephemeral`); `altacli.jsonl` is the trace.
- `altacli` needs libssl and CA certificates: the adapter installs them during setup when an image lacks them.
- Only `linux-x64` images with glibc 2.28 or later (all SWE-bench Pro v2 images are `linux/amd64`); a musl image would need a `linux-musl-x64` build.
- Exit codes 1, 2, 5 and 6 of `altacli` are reported to Harbor as agent errors (classified as rate limit, authentication... for `--retry-include`); the patch is still captured and graded.
