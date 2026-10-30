"""Blind pairwise judge of the patches of two HARD-51 jobs (#273): which change would a maintainer rather merge.

The tests of SWE-bench Pro say whether a patch works, not whether it adds debt. For each task both jobs captured a
patch for, a model reads the instruction of the task and the two patches, in an order drawn at random and named X and Y
only, and says which it would merge and how each scores. The judge runs through ``altacli run`` without tools, so it can
be any model CodeAlta has (a subscription included).

    python judge-patches.py --a jobs/<run>-codealta-opus55-xhigh --b jobs/<run>-codealta-haiku55-high-escalate-... \
        --dataset datasets/hard51 --altacli <altacli> --model codex:gpt-6.1-sol@medium --out results/<name>.md
"""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import random
import re
import subprocess
from pathlib import Path

_PATCH_LIMIT = 60_000
_CRITERIA = ("correctness", "cause", "design", "tests", "overall")

_PROMPT = """You review two patches that address the same issue of an open-source repository, for the maintainer who will
merge one. Hidden tests already decide whether each works; judge what they do not see. Score each patch from 1 (poor)
to 5 (excellent):
- correctness: right beyond the tests, on the cases the issue states and the inputs callers give;
- cause: the problem fixed where it comes from, not worked around where it shows;
- design: simple, no duplication, fits the code and the conventions of the repository, nothing the issue does not need;
- tests: the tests the patch adds or changes check the behavior, its edge cases included (1 when it adds none and should);
- overall: would you merge it as it is.
Then say which patch you would rather merge. Answer with one JSON object alone:
{"X": {"correctness": n, "cause": n, "design": n, "tests": n, "overall": n, "debt": "main debt in one line, or none"},
 "Y": {...same...}, "prefer": "X" | "Y" | "same"}

## The issue

{instruction}

## Patch X

~~~diff
{x}
~~~

## Patch Y

~~~diff
{y}
~~~
"""


def _trials(job: Path) -> dict[str, tuple[Path, float | None]]:
    """The trials of a job by task: the folder of the trial and its reward (None when not graded)."""
    found: dict[str, tuple[Path, float | None]] = {}
    for result in job.glob("*/result.json"):
        try:
            data = json.loads(result.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        task = str(data.get("task_name") or "").split("/")[-1]
        rewards = (data.get("verifier_result") or {}).get("rewards") or {}
        reward = rewards.get("reward") if isinstance(rewards, dict) else None
        if task:
            found[task] = (result.parent, float(reward) if isinstance(reward, (int, float)) else None)
    return found


def _patch(trial: Path) -> str:
    path = trial / "agent" / "model.patch"
    text = path.read_text(encoding="utf-8", errors="replace") if path.is_file() else ""
    return text if len(text) <= _PATCH_LIMIT else text[:_PATCH_LIMIT] + "\n[cut]"


def _judge(altacli: str, model: str, prompt: str) -> dict | None:
    process = subprocess.run(
        [altacli, "run", "--ephemeral", "--quiet", "--no-mcp", "--no-plugins", "--no-skills", "-m", model,
         "--timeout", "600", "--disable-tools", "*", "-"],
        input=prompt.encode("utf-8"), capture_output=True, timeout=700)
    answer = process.stdout.decode("utf-8", errors="replace")
    match = re.search(r"\{.*\}", answer, re.DOTALL)
    if not match:
        return None
    try:
        return json.loads(match.group(0))
    except ValueError:
        return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--a", required=True, type=Path, help="the folder of the first job (the reference)")
    parser.add_argument("--b", required=True, type=Path, help="the folder of the second job")
    parser.add_argument("--dataset", required=True, type=Path, help="the dataset folder, with <task>/instruction.md")
    parser.add_argument("--altacli", required=True)
    parser.add_argument("--model", required=True, help="the judge, as altacli -m takes it")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--parallel", type=int, default=4)
    parser.add_argument("--seed", type=int, default=273)
    args = parser.parse_args()

    a, b = _trials(args.a), _trials(args.b)
    tasks = sorted(set(a) & set(b))
    rng = random.Random(args.seed)
    jobs = []
    for task in tasks:
        instruction_path = args.dataset / task / "instruction.md"
        instruction = instruction_path.read_text(encoding="utf-8") if instruction_path.is_file() else task
        swap = rng.random() < 0.5
        pa, pb = _patch(a[task][0]), _patch(b[task][0])
        x, y = (pb, pa) if swap else (pa, pb)
        jobs.append((task, swap, _PROMPT.format(instruction=instruction[:20_000], x=x or "(empty)", y=y or "(empty)")))

    rows = []
    with concurrent.futures.ThreadPoolExecutor(args.parallel) as pool:
        futures = {pool.submit(_judge, args.altacli, args.model, prompt): (task, swap) for task, swap, prompt in jobs}
        for future in concurrent.futures.as_completed(futures):
            task, swap = futures[future]
            verdict = future.result()
            if not verdict:
                rows.append({"task": task, "verdict": None})
                continue
            # Back from X and Y to A and B.
            side = {"X": "B" if swap else "A", "Y": "A" if swap else "B", "same": "same"}
            rows.append({"task": task, "A": verdict.get("Y" if swap else "X"), "B": verdict.get("X" if swap else "Y"),
                         "prefer": side.get(str(verdict.get("prefer")), None),
                         "reward_a": a[task][1], "reward_b": b[task][1]})

    judged = [row for row in rows if row.get("prefer")]
    lines = [f"# Patch quality: {args.a.name} (A) against {args.b.name} (B)", "",
             f"Judge: `{args.model}`, blind (patches named X and Y in a random order). {len(judged)} of {len(tasks)} tasks judged.", "",
             "| | A | B |", "| --- | --- | --- |"]
    for criterion in _CRITERIA:
        values = {side: [row[side][criterion] for row in judged if isinstance(row.get(side), dict) and isinstance(row[side].get(criterion), (int, float))] for side in ("A", "B")}
        lines.append(f"| {criterion} | " + " | ".join(f"{sum(v) / len(v):.2f}" if v else "–" for v in values.values()) + " |")
    prefer = {side: sum(1 for row in judged if row["prefer"] == side) for side in ("A", "B", "same")}
    lines += ["", f"Preferred: A {prefer['A']}, B {prefer['B']}, same {prefer['same']}.", "",
              "| Task | Reward A | Reward B | Overall A | Overall B | Preferred | Debt of B |", "| --- | --- | --- | --- | --- | --- | --- |"]
    for row in sorted(judged, key=lambda item: item["task"]):
        debt = str((row.get("B") or {}).get("debt") or "").replace("|", "\\|")
        lines.append(f"| {row['task'][:60]} | {row['reward_a']} | {row['reward_b']} | {(row.get('A') or {}).get('overall')} | {(row.get('B') or {}).get('overall')} | {row['prefer']} | {debt[:140]} |")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    (args.out.with_suffix(".json")).write_text(json.dumps(rows, indent=1), encoding="utf-8")
    print("\n".join(lines[:16]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
