"""Summarizes a HARD-51 run of run-hard51.ps1 into results/<run-id>-summary.md and .json.

Scores come from the pristine replays (the -regrade jobs). Costs are estimated from the tokens at list prices for every
run, the same way for all four: those prices reproduce Claude Code's own total_cost_usd and Harbor's Codex cost_usd
exactly. Harbor's reported cost is kept beside it: for Claude Code it misses the segments of a trial that restarted.

A run made with -Runs (for example only the CodeAlta jobs) is summarized with the jobs it has; --baseline-run-id takes
the missing jobs from an earlier run of the same jobs root, such as the native harnesses of a reference run.

Usage: python summarize-results.py [--run-id ID] [--baseline-run-id ID] [--jobs-root DIR] [--benchmark-root DIR]
"""
from __future__ import annotations

import argparse
import ast
import json
import math
import re
import statistics
from collections import defaultdict
from datetime import datetime
from pathlib import Path


# label -> (job suffix, harness kind, model)
RUNS = {
    "CodeAlta / Opus": ("codealta-opus55-xhigh", "codealta", "opus"),
    "Claude Code / Opus": ("claude-code-opus55-xhigh", "claude-code", "opus"),
    "CodeAlta / Astra": ("codealta-astra-xhigh", "codealta", "astra"),
    "Codex / Astra": ("codex-astra-xhigh", "codex", "astra"),
}

# Per token: uncached input, cache read, cache write, output (Claude Opus 5.5; GPT-6 Astra under 272k tokens).
PRICES = {
    "opus": (4e-6, 0.2e-6, 5e-6, 20e-6),
    "astra": (10e-6, 1e-6, 12.5e-6, 50e-6),
}


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def timestamp(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def list_value(value) -> list:
    if isinstance(value, list):
        return value
    if isinstance(value, str):
        try:
            parsed = json.loads(value)
        except Exception:
            parsed = ast.literal_eval(value)
        return parsed if isinstance(parsed, list) else []
    return []


def percentile(values: list[float], fraction: float) -> float:
    values = sorted(values)
    return values[min(len(values) - 1, math.ceil(len(values) * fraction) - 1)]


def discover_run_id(jobs: Path) -> str:
    manifests = sorted(jobs.glob("*-manifest.json"), key=lambda path: path.stat().st_mtime)
    if not manifests:
        raise RuntimeError(f"No run manifest found in {jobs}")
    return manifests[-1].name.removesuffix("-manifest.json")


def task_repo(task: str) -> str:
    owner, remainder = task.removeprefix("instance_").split("__", 1)
    match = re.match(r"(.+?)-[0-9a-f]{40}(?:-|$)", remainder)
    repository = match.group(1) if match else remainder.split("-", 1)[0]
    return f"{owner}/{repository}"


def assert_job_complete(path: Path) -> None:
    job_result = load_json(path)
    stats = job_result["stats"]
    if (
        job_result.get("finished_at") is None
        or stats["n_errored_trials"] != 0
        or stats["n_running_trials"] != 0
        or stats["n_pending_trials"] != 0
    ):
        raise RuntimeError(f"Incomplete or errored job: {path}")


def cache_write_tokens(kind: str, directory: Path) -> int:
    """Cache-write tokens of one trial: CodeAlta's result line, or the trajectory of a native harness."""
    if kind == "codealta":
        last = json.loads((directory / "agent" / "altacli.jsonl").read_text(encoding="utf-8").splitlines()[-1])
        usage = last.get("params", {}).get("usage") or {}
        return usage.get("cachedWriteTokens") or 0
    trajectory = directory / "agent" / "trajectory.json"
    if not trajectory.exists():
        return 0
    extra = (load_json(trajectory).get("final_metrics") or {}).get("extra") or {}
    # Claude Code names them cache creation, Codex cache write.
    return extra.get("total_cache_creation_input_tokens") or extra.get("total_cache_write_input_tokens") or 0


parser = argparse.ArgumentParser()
parser.add_argument("--bundle-root", type=Path, default=Path(__file__).resolve().parents[1])
parser.add_argument("--run-id")
parser.add_argument("--baseline-run-id", help="takes the jobs the run lacks from this run")
parser.add_argument("--jobs-root", type=Path)
parser.add_argument("--benchmark-root", type=Path)
parser.add_argument("--output-root", type=Path)
args = parser.parse_args()

bundle = args.bundle_root.resolve()
workspace = load_json(bundle / "workspace.json") if (bundle / "workspace.json").exists() else {}
jobs = (args.jobs_root or bundle / "jobs").resolve()
results = (args.output_root or bundle / "results").resolve()
if args.benchmark_root is None and "benchmark_root" not in workspace:
    raise RuntimeError("No benchmark checkout: run scripts/setup.ps1, or pass --benchmark-root.")
benchmark = (args.benchmark_root or Path(workspace["benchmark_root"])).resolve()
tasks_root = benchmark / "v2" / "tasks"
run_id = args.run_id or discover_run_id(jobs)

summary = {}
outcomes = {}
sources = {}
repositories: dict[str, dict[str, int]] = defaultdict(lambda: {"tasks": 0})

for label, (suffix, kind, model) in RUNS.items():
    source_run = None
    for candidate in [run_id] + ([args.baseline_run_id] if args.baseline_run_id else []):
        if (jobs / f"{candidate}-{suffix}" / "result.json").exists():
            source_run = candidate
            break
    if source_run is None:
        print(f"skipped {label}: no {run_id}-{suffix} job" + (f" nor {args.baseline_run_id}-{suffix}" if args.baseline_run_id else ""))
        continue
    model_job = jobs / f"{source_run}-{suffix}"
    regrade_job = jobs / f"{source_run}-{suffix}-regrade"
    for path in (model_job / "result.json", regrade_job / "result.json"):
        if not path.exists():
            raise RuntimeError(f"Missing job result: {path}")
        assert_job_complete(path)
    sources[label] = source_run

    model_rows = {}
    for directory in model_job.glob("instance_*"):
        record = load_json(directory / "result.json")
        task = record["task_name"].split("/")[-1]
        agent = record.get("agent_result") or {}
        phase = record.get("agent_execution") or {}
        model_rows[task] = {
            "directory": directory,
            "input": agent.get("n_input_tokens") or 0,
            "cache_read": agent.get("n_cache_tokens") or 0,
            "cache_write": cache_write_tokens(kind, directory),
            "output": agent.get("n_output_tokens") or 0,
            "cost": agent.get("cost_usd"),
            "agent_seconds": (
                timestamp(phase["finished_at"]) - timestamp(phase["started_at"])
            ).total_seconds(),
            "end_to_end_seconds": (
                timestamp(record["finished_at"]) - timestamp(record["started_at"])
            ).total_seconds(),
        }

    rewards = {}
    required = {"total": 0, "passed": 0, "failed": 0, "missing": 0}
    selected = {"passed": 0, "failed": 0, "skipped": 0, "error": 0}
    failures = []

    for directory in regrade_job.glob("instance_*"):
        record = load_json(directory / "result.json")
        task = record["task_name"].split("/")[-1]
        reward = (record.get("verifier_result") or {}).get("rewards", {}).get("reward")
        rewards[task] = reward
        tests = load_json(directory / "verifier" / "output.json").get("tests", [])
        statuses = {test.get("name"): test.get("status") for test in tests}
        for status in statuses.values():
            key = (status or "").lower()
            selected[key] = selected.get(key, 0) + 1

        config = load_json(tasks_root / task / "tests" / "config.json")
        scoring = list_value(config.get("fail_to_pass")) + list_value(config.get("pass_to_pass"))
        required["total"] += len(scoring)
        task_failures = []
        task_missing = []
        for test in scoring:
            status = statuses.get(test)
            if status == "PASSED":
                required["passed"] += 1
            elif status is None:
                required["missing"] += 1
                task_missing.append(test)
            else:
                required["failed"] += 1
                task_failures.append(test)
        if reward == 0:
            failures.append({"task": task, "failed": task_failures, "missing": task_missing})

    if len(rewards) != 51 or set(rewards) != set(model_rows):
        raise RuntimeError(f"Model/regrade task mismatch for {label}")

    input_tokens = sum(row["input"] for row in model_rows.values())
    cache_read = sum(row["cache_read"] for row in model_rows.values())
    cache_write = sum(row["cache_write"] for row in model_rows.values())
    output_tokens = sum(row["output"] for row in model_rows.values())
    reported = [row["cost"] for row in model_rows.values() if row["cost"] is not None]
    rates = PRICES[model]
    estimated_cost = (
        (input_tokens - cache_read - cache_write) * rates[0]
        + cache_read * rates[1]
        + cache_write * rates[2]
        + output_tokens * rates[3]
    )

    agent_seconds = [row["agent_seconds"] for row in model_rows.values()]
    end_to_end = [row["end_to_end_seconds"] for row in model_rows.values()]
    job = load_json(model_job / "result.json")
    wall_minutes = (timestamp(job["finished_at"]) - timestamp(job["started_at"])).total_seconds() / 60

    summary[label] = {
        "run_id": source_run,
        "solved": sum(reward == 1 for reward in rewards.values()),
        "required": required,
        "selected": selected,
        "failures": failures,
        "input_tokens": input_tokens,
        "cache_read_tokens": cache_read,
        "cache_write_tokens": cache_write,
        "uncached_input_tokens": input_tokens - cache_read - cache_write,
        "output_tokens": output_tokens,
        "cache_ratio": cache_read / input_tokens if input_tokens else 0.0,
        "estimated_cost_usd": estimated_cost,
        "reported_cost_usd": sum(reported) if reported else None,
        "wall_minutes": wall_minutes,
        "agent_hours": sum(agent_seconds) / 3600,
        "mean_agent_minutes": statistics.mean(agent_seconds) / 60,
        "median_agent_minutes": statistics.median(agent_seconds) / 60,
        "p90_agent_minutes": percentile(agent_seconds, 0.90) / 60,
        "max_agent_minutes": max(agent_seconds) / 60,
        "mean_end_to_end_minutes": statistics.mean(end_to_end) / 60,
    }
    outcomes[label] = rewards

    for task, reward in rewards.items():
        repository = task_repo(task)
        repositories[repository].setdefault("_tasks", set()).add(task)
        repositories[repository][label] = repositories[repository].get(label, 0) + (1 if reward == 1 else 0)

if not summary:
    raise RuntimeError(f"No job of run {run_id} in {jobs}")

for row in repositories.values():
    row["tasks"] = len(row.pop("_tasks"))

pairs = {}
for name, codealta, native in (
    ("Opus", "CodeAlta / Opus", "Claude Code / Opus"),
    ("Astra", "CodeAlta / Astra", "Codex / Astra"),
):
    if codealta not in outcomes or native not in outcomes:
        continue
    task_ids = sorted(outcomes[codealta])
    pairs[name] = {
        "both_pass": sum(outcomes[codealta][task] == 1 and outcomes[native][task] == 1 for task in task_ids),
        "codealta_only": sum(outcomes[codealta][task] == 1 and outcomes[native][task] == 0 for task in task_ids),
        "native_only": sum(outcomes[codealta][task] == 0 and outcomes[native][task] == 1 for task in task_ids),
        "both_fail": sum(outcomes[codealta][task] == 0 and outcomes[native][task] == 0 for task in task_ids),
        "discordant": [
            {"task": task, "codealta": outcomes[codealta][task], "native": outcomes[native][task]}
            for task in task_ids
            if outcomes[codealta][task] != outcomes[native][task]
        ],
    }

output = {"run_id": run_id, "baseline_run_id": args.baseline_run_id, "sources": sources, "summary": summary,
          "pairs": pairs, "repositories": repositories}
results.mkdir(parents=True, exist_ok=True)
(results / f"{run_id}-summary.json").write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")

labels = list(summary)
lines = [
    f"# SWE-bench Pro V2 HARD-51 results — {run_id}",
    "",
    "Authoritative outcomes use pristine patch replay. Estimated costs use the same list prices for every run.",
    "",
    "## Scorecard",
    "",
    "| Metric | " + " | ".join(labels) + " |",
    "|---|" + "---:|" * len(labels),
]
if any(summary[label]["run_id"] != run_id for label in labels):
    lines.append("| Run | " + " | ".join(f"`{summary[label]['run_id']}`" for label in labels) + " |")
lines.append("| Resolved tasks | " + " | ".join(f"**{summary[label]['solved']}/51**" for label in labels) + " |")
lines.append("| Score | " + " | ".join(f"**{summary[label]['solved'] / 51:.2%}**" for label in labels) + " |")
lines.append("| Required tests | " + " | ".join(
    f"{summary[label]['required']['passed']:,}/{summary[label]['required']['total']:,}"
    for label in labels
) + " |")
lines.append("| Required-test pass rate | " + " | ".join(
    f"{summary[label]['required']['passed'] / summary[label]['required']['total']:.3%}"
    for label in labels
) + " |")
lines.append("| Model-job wall time | " + " | ".join(f"{summary[label]['wall_minutes']:.1f}m" for label in labels) + " |")
lines.append("| Total agent-hours | " + " | ".join(f"{summary[label]['agent_hours']:.2f}h" for label in labels) + " |")
lines.append("| Mean agent runtime | " + " | ".join(f"{summary[label]['mean_agent_minutes']:.2f}m" for label in labels) + " |")
lines.append("| Median agent runtime | " + " | ".join(f"{summary[label]['median_agent_minutes']:.2f}m" for label in labels) + " |")
lines.append("| P90 agent runtime | " + " | ".join(f"{summary[label]['p90_agent_minutes']:.2f}m" for label in labels) + " |")
lines.append("| Input tokens | " + " | ".join(f"{summary[label]['input_tokens'] / 1_000_000:.2f}M" for label in labels) + " |")
lines.append("| Cache writes | " + " | ".join(f"{summary[label]['cache_write_tokens'] / 1_000_000:.2f}M" for label in labels) + " |")
lines.append("| Output tokens | " + " | ".join(f"{summary[label]['output_tokens'] / 1_000_000:.2f}M" for label in labels) + " |")
lines.append("| Cache-read ratio | " + " | ".join(f"{summary[label]['cache_ratio']:.2%}" for label in labels) + " |")
lines.append("| Estimated cost | " + " | ".join(f"${summary[label]['estimated_cost_usd']:.2f}" for label in labels) + " |")
lines.append("| Reported cost (Harbor) | " + " | ".join(
    f"${summary[label]['reported_cost_usd']:.2f}" if summary[label]["reported_cost_usd"] is not None else "n/a"
    for label in labels
) + " |")

if pairs:
    lines.extend(["", "## Pairwise outcomes", "", "| Model | Both pass | CodeAlta only | Native only | Both fail |", "|---|---:|---:|---:|---:|"])
    for name, pair in pairs.items():
        lines.append(f"| {name} | {pair['both_pass']} | {pair['codealta_only']} | {pair['native_only']} | {pair['both_fail']} |")

lines.extend(["", "## Failed tasks", ""])
for label in labels:
    lines.append(f"### {label}")
    lines.append("")
    if not summary[label]["failures"]:
        lines.append("None.")
    for failure in summary[label]["failures"]:
        lines.append(f"- `{failure['task']}`")
        for test in failure["failed"]:
            lines.append(f"  - failed: `{test}`")
        for test in failure["missing"]:
            lines.append(f"  - missing: `{test}`")
    lines.append("")

lines.extend(["## Scores by repository", "", "| Repository | Tasks | " + " | ".join(labels) + " |", "|---|---:|" + "---:|" * len(labels)])
for repository in sorted(repositories):
    row = repositories[repository]
    lines.append(
        f"| `{repository}` | {row['tasks']} | "
        + " | ".join(f"{row.get(label, 0)}/{row['tasks']}" for label in labels)
        + " |"
    )

(results / f"{run_id}-summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(results / f"{run_id}-summary.md")
