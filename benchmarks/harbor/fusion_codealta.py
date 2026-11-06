"""Multi-model patterns over ``altacli run``, after GitHub Copilot Fusion and Devin Fusion (#273 follow-up).

The primary model works as ``LockedCodeAlta`` runs it; other models of any provider then act on its work, each in a
session of its own that receives only what it needs (the request and the diff), so that the session of the primary
keeps its cache:

  * ``steps=critique``: read-only critics (``critics=``, in parallel) review the change; when one asks for a revision,
    the session of the primary is resumed with their findings, which it checks and fixes. A critic that gives no
    usable verdict leaves the draft as it is. What a critic changed in the repository is undone.
  * ``steps=cascade``: a read-only judge (``judge=``) accepts the change or escalates it; on escalation a repair model
    (``repair=``) starts a session of its own on the request, with the draft in the working tree and the judge's reasons.
  * ``steps=recheck``: the recheck of ``LockedCodeAlta`` (the primary checks its change against the request).
  * Steps combine in order: ``steps=recheck,critique``.

Model references are ``provider:model@effort`` with the providers of ``_PROVIDERS`` (``anthropic``, ``openai``...); the
primary is ``-m provider/model`` with ``--ak reasoning_effort=``. Every leg is recorded in the metadata of the trial
(``fusion``: role, model, verdict, usage, cost altacli estimated from public prices), with the total in
``codealta_estimated_cost_usd``.

Usage::

    PYTHONPATH=v2/tooling:<codealta>/benchmarks/harbor harbor run -p <tasks> -e docker \
      -a fusion_codealta:FusionCodeAlta -m openai/gpt-6.1-sol --ak reasoning_effort=medium \
      --ak steps=critique --ak critics=anthropic:claude-opus-5-5@high --ak timeout_sec=600 \
      --allow-agent-host api.openai.com --allow-agent-host api.anthropic.com
"""
from __future__ import annotations

import asyncio
import json
import re
import shlex
import tempfile
import time
import uuid
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from harbor.agents.installed.base import AgentAuthenticationError
from harbor.environments.base import BaseEnvironment
from harbor.models.agent.context import AgentContext

from locked_codealta import (
    _ALTACLI,
    _CAPTURE,
    _CHANGED,
    _EFFORTS,
    _FIND_REPO,
    _JSONL,
    _LOGS,
    _PROFILE_DIR,
    _PROVIDERS,
    _RUN_DIR,
    _STDERR,
    LockedCodeAlta,
    _toml_string,
)

_VERDICT = re.compile(r"^\W*VERDICT\W*:?\W*(APPROVE|REVISE|ACCEPT|ESCALATE)\b", re.IGNORECASE | re.MULTILINE)
_FINDINGS_MAX = 8000
_HELPER_MIN_SEC = 120
# The write tools of CodeAlta: a reviewer works without them. The shell stays, to read and run tests; what it changes
# in the repository is undone after the review.
_READ_ONLY_TOOLS = "web,write_file,replace_in_file,apply_patch,delete_file_or_dir,rename_file_or_dir"

# The tree of the working folder (new files included, .alta/ left out) in a temporary index, and the draft diff against
# the base commit; then the repository restored to that tree after a reviewer ran.
_SNAPSHOT = (
    f"repo=$(cat {_RUN_DIR}/repo 2>/dev/null || {{ {_FIND_REPO}; }}); base=$(cat {_RUN_DIR}/base 2>/dev/null); "
    'cd "$repo" || exit 3; '
    f"export GIT_INDEX_FILE={_RUN_DIR}/snapshot.index; rm -f \"$GIT_INDEX_FILE\"; "
    "git -c safe.directory='*' add -A -- . ':(exclude).alta' 2>/dev/null; "
    f"tree=$(git -c safe.directory='*' write-tree) && echo \"$tree\" > {_RUN_DIR}/snapshot.tree; "
    f"git -c safe.directory='*' diff ${{base:-HEAD}} \"$tree\" -- . ':(exclude).alta' > {_RUN_DIR}/draft.patch 2>/dev/null; "
    f"chmod a+r {_RUN_DIR}/draft.patch {_RUN_DIR}/snapshot.tree; wc -c < {_RUN_DIR}/draft.patch"
)
_RESTORE = (
    f"repo=$(cat {_RUN_DIR}/repo 2>/dev/null || {{ {_FIND_REPO}; }}); tree=$(cat {_RUN_DIR}/snapshot.tree 2>/dev/null); "
    'cd "$repo" && [ -n "$tree" ] || exit 0; '
    f"export GIT_INDEX_FILE={_RUN_DIR}/restore.index; rm -f \"$GIT_INDEX_FILE\"; "
    "git -c safe.directory='*' add -A -- . ':(exclude).alta' 2>/dev/null; now=$(git -c safe.directory='*' write-tree); "
    'if [ "$now" = "$tree" ]; then echo RESTORED=0; exit 0; fi; '
    "git -c safe.directory='*' diff --name-only --no-renames --diff-filter=A \"$tree\" \"$now\" -z | xargs -0 -r rm -f --; "
    "rm -f \"$GIT_INDEX_FILE\"; git -c safe.directory='*' read-tree \"$tree\" && git -c safe.directory='*' checkout-index -a -f; "
    "echo RESTORED=1"
)


def parse_model_ref(ref: str) -> tuple[str, str, str | None]:
    """``provider:model@effort`` as (provider, model, effort); the effort is optional."""
    text = ref.strip()
    if ":" not in text:
        raise ValueError(f"a model reference is provider:model[@effort]; got {ref!r}")
    provider, rest = text.split(":", 1)
    model, _, effort = rest.partition("@")
    effort = effort.strip().lower() or None
    if effort is not None and effort not in _EFFORTS:
        raise ValueError(f"unknown reasoning effort {effort!r} in {ref!r}")
    if not provider.strip() or not model.strip():
        raise ValueError(f"a model reference is provider:model[@effort]; got {ref!r}")
    return provider.strip().lower(), model.strip(), effort


def parse_verdict(answer: str | None) -> tuple[str | None, str]:
    """The last verdict line of an answer, upper case, and the findings before it; (None, answer) without a verdict."""
    text = answer or ""
    matches = list(_VERDICT.finditer(text))
    if not matches:
        return None, text.strip()
    last = matches[-1]
    findings = (text[:last.start()] + text[last.end():]).strip()
    return last.group(1).upper(), findings[:_FINDINGS_MAX]


_DIFF_MAX = 60000


def diff_prompt(instruction: str, diff: str, judge: bool) -> str:
    """The review of a change from its diff alone, without tools: the cheapest review (``critic_mode=diff``)."""
    verdicts = ("`VERDICT: ACCEPT` if the change solves the request correctly, or `VERDICT: ESCALATE` if it does not and a "
                "stronger engineer must take over") if judge else \
               ("`VERDICT: APPROVE` if the change is right as it is, or `VERDICT: REVISE` if at least one problem must be fixed")
    shown = diff if len(diff) <= _DIFF_MAX else diff[:_DIFF_MAX] + "\n[... diff truncated ...]"
    return f"""You review a change that another engineer (another AI model) made to solve the request below. You have no tools: judge from the request and the diff.

## The request

{instruction}

## The diff against the starting commit

```diff
{shown}
```

## What to check

- Does it implement every requirement of the request, with the names, signatures, messages and behaviours it states?
- Is it correct on the edge cases and the error paths? Does it fix the cause rather than a symptom? Could it break existing behaviour?

Report only real problems you can point to in the diff, each with the file, what is wrong and why it matters, most important first. Do not report style, optional improvements, or doubts about code you cannot see.

End your answer with exactly one line: {verdicts}.
"""


def critic_prompt(instruction: str, repo: str, judge: bool) -> str:
    verdicts = ("`VERDICT: ACCEPT` if the change solves the request correctly, or `VERDICT: ESCALATE` if it does not and a "
                "stronger engineer must take over") if judge else \
               ("`VERDICT: APPROVE` if the change is right as it is, or `VERDICT: REVISE` if at least one problem must be fixed")
    return f"""You review a change that another engineer (another AI model) made to solve the request below. You are read-only: do not modify, create or delete any file of the repository; a scratch copy under /tmp is fine. Read the code, and run the tests or the commands you need to check the change.

## The request

{instruction}

## The change

It is applied in the working tree of {repo}; its diff against the starting commit is in {_RUN_DIR}/draft.patch.

## What to check

- Does it implement every requirement of the request, with the names, signatures, messages and behaviours it states?
- Is it correct on the edge cases and the error paths? Does it fix the cause rather than a symptom? Does it break existing behaviour or tests?

Report only problems you verified in the code or by a run, each with the file, what is wrong and why it matters, most important first. Do not report style or optional improvements.

End your answer with exactly one line: {verdicts}.
"""


def state_prompt(instruction: str, workdir: str, report: str | None, judge: bool) -> str:
    """The review of a task whose result is the state of the machine (no git repository to diff): the request, the
    report of the engineer, and the environment to inspect."""
    verdicts = ("`VERDICT: ACCEPT` if the task is done correctly, or `VERDICT: ESCALATE` if it is not and a stronger "
                "engineer must take over") if judge else                ("`VERDICT: APPROVE` if the task is done correctly, or `VERDICT: REVISE` if at least one problem must be fixed")
    return f"""Another engineer (another AI model) worked on the task below in this machine, from {workdir}. Check whether the task is really done. You are read-only: inspect files, processes and outputs, and run the checks you need, but do not change the state of the machine (no edits, no installs, no service changes); a scratch copy under /tmp is fine.

## The task

{instruction}

## The engineer's report

{(report or "(no report)").strip()[:6000]}

## What to check

Every requirement of the task, exactly as stated: the paths, names, formats, values and behaviours it asks for, and what a checker of the task would test. Report only problems you verified, each with what is wrong and why it matters, most important first.

End your answer with exactly one line: {verdicts}.
"""


def revision_prompt(findings: list[str]) -> str:
    body = "\n\n---\n\n".join(findings)
    return f"""Independent reviewers (other AI models, read-only) checked your change against the request. Their findings:

{body}

Check each finding yourself. Fix the ones that are real and keep the tests passing; ignore the ones that are wrong. Then answer as you would have."""


def repair_prompt(instruction: str, reasons: str) -> str:
    return f"""{instruction}

---

Note: a first attempt by another engineer left changes in the working tree (its diff against the starting commit is in {_RUN_DIR}/draft.patch). A reviewer found these problems with it:

{reasons}

Keep what is right in the attempt, fix or redo what is not, and make sure the request is fully done."""


def state_repair_prompt(instruction: str, reasons: str) -> str:
    return f"""{instruction}

---

Note: a first attempt by another engineer already worked on this task in this machine. A reviewer found these problems with it:

{reasons}

Check what is there, keep what is right, fix or redo what is not, and make sure the task is fully done."""


def _leg_record(role: str, model_ref: str, result: dict[str, Any] | None, exit_code: int | None, **extra: Any) -> dict[str, Any]:
    cost = (result or {}).get("cost")
    amount = cost.get("amount") if isinstance(cost, dict) and isinstance(cost.get("amount"), (int, float)) else None
    record = {
        "role": role,
        "model": model_ref,
        "exit_code": exit_code,
        "status": (result or {}).get("status"),
        "duration_ms": (result or {}).get("durationMs"),
        "usage": (result or {}).get("usage"),
        "cost_usd": amount,
    }
    record.update(extra)
    return record


class FusionCodeAlta(LockedCodeAlta):
    """``LockedCodeAlta`` followed by the steps of other models: see the module documentation.

    Agent kwargs, besides those of ``LockedCodeAlta`` (``timeout_sec`` is the budget of the primary run):
      steps              comma-separated: recheck, critique, cascade (default: none, the primary alone)
      critics            comma-separated model references of the critics (critique)
      judge              the model reference of the judge (cascade)
      repair             the model reference of the repair (cascade)
      helper_timeout_sec the budget of each review, revision and repair (default 360)
      critic_mode        tools (default: read-only tools, runs tests) or diff (the diff in the prompt, no tool; git only)
      adaptive           low, medium, high or max: CodeAlta's own Adaptive runs the task (altacli --tier adaptive:<level>),
                         the level made of -m and its effort for the turns and critics= for its critics, reviewed by the
                         host itself; no step of this adapter runs then
    """

    def __init__(self, *args: Any, steps: str | None = None, critics: str | None = None, judge: str | None = None,
                 repair: str | None = None, helper_timeout_sec: int | str | None = None, critic_mode: str | None = None,
                 adaptive: str | None = None, **kwargs: Any):
        recheck = "recheck" in [s.strip().lower() for s in (steps or "").split(",")]
        super().__init__(*args, recheck=recheck, **kwargs)
        self._steps = [s.strip().lower() for s in (steps or "").split(",") if s.strip()]
        unknown = [s for s in self._steps if s not in ("recheck", "critique", "cascade")]
        if unknown:
            raise ValueError(f"unknown steps: {', '.join(unknown)}")
        self._critics = [c.strip() for c in (critics or "").split(",") if c.strip()]
        for critic in self._critics:
            parse_model_ref(critic)
        self._judge = judge.strip() if judge else None
        self._repair = repair.strip() if repair else None
        if "critique" in self._steps and not self._critics:
            raise ValueError("steps=critique needs critics=")
        if "cascade" in self._steps and not (self._judge and self._repair):
            raise ValueError("steps=cascade needs judge= and repair=")
        for ref in filter(None, (self._judge, self._repair)):
            parse_model_ref(ref)
        self._helper_sec = int(helper_timeout_sec) if helper_timeout_sec is not None else 360
        self._adaptive = adaptive.strip().lower() if adaptive else None
        if self._adaptive is not None and self._adaptive not in ("low", "medium", "high", "max"):
            raise ValueError(f"adaptive must be low, medium, high or max; got {adaptive!r}")
        if self._adaptive is not None and self._steps:
            raise ValueError("adaptive runs CodeAlta's own review: name no steps with it")
        self._critic_mode = (critic_mode or "tools").strip().lower()
        if self._critic_mode not in ("tools", "diff"):
            raise ValueError(f"critic_mode must be tools or diff; got {critic_mode!r}")
        # The session of the primary is kept, to be resumed by a recheck or a revision.
        self._session_id = str(uuid.uuid4())
        self._legs: list[dict[str, Any]] = []
        self._primary_result: dict[str, Any] | None = None

    @staticmethod
    def name() -> str:
        return "codealta-fusion"

    # ----- providers -----

    def _refs(self) -> list[str]:
        return [*self._critics, *filter(None, (self._judge, self._repair))]

    def _multi_settings(self) -> tuple[str, list[str], dict[str, str]]:
        """The primary model reference, the -c settings of every provider used, and their keys."""
        model_ref, settings, key_name, key_value = self._model_settings()
        env = {key_name: key_value}
        primary_provider = model_ref.split(":", 1)[0]
        for ref in self._refs():
            provider, _, _ = parse_model_ref(ref)
            if provider == primary_provider or f"providers.{provider}.type=" in " ".join(settings):
                continue
            known = _PROVIDERS.get(provider)
            if known is None:
                raise ValueError(f"unknown provider {provider!r} in {ref!r}")
            provider_type, key_names, _ = known
            value = next(((name, self._get_env(name)) for name in key_names if self._get_env(name)), None)
            if value is None:
                raise AgentAuthenticationError(f"no API key for {provider}: set {' or '.join(key_names)}")
            env[value[0]] = value[1]
            settings += [f"providers.{provider}.type={_toml_string(provider_type)}",
                         f"providers.{provider}.api_key_env={_toml_string(value[0])}"]
        return model_ref, settings, env

    def _helper_command(self, repo: str, model_ref: str, settings: list[str], budget_sec: int, session_id: str,
                        prompt_file: str, jsonl: str, stderr: str, read_only: bool | str) -> str:
        provider = model_ref.split(":", 1)[0]
        # A profile of its own: critics run at the same time, and none is resumed.
        profile = f"{_PROFILE_DIR}-{session_id[:8]}"
        args = [
            _ALTACLI, "run", "--ephemeral", "--profile", profile, "--ignore-user-config",
            "-C", repo, f"--session-id={session_id}", "-m", model_ref,
            "--offline", "--format", "jsonl", "--timeout", str(budget_sec - 90),
            "--disable-tools", "*" if read_only == "none" else _READ_ONLY_TOOLS if read_only else (self._disable_tools or "web"),
            "--no-plugins", "--no-mcp", "--no-sub-sessions",
        ]
        if self._prompt_id:
            args += ["--prompt-id", self._prompt_id]
        # The default provider of a helper is its own: settings name the primary's.
        for setting in settings:
            if not setting.startswith("chat.default_provider="):
                args += ["-c", setting]
        args += ["-c", f"chat.default_provider={_toml_string(provider)}", "-"]
        run = " ".join(shlex.quote(a) for a in args)
        return (f"mkdir -p {_LOGS} {profile}; cd {shlex.quote(repo)} && "
                f"timeout -k 30 {budget_sec - 60} {run} < {prompt_file} > {jsonl} 2> {stderr}; "
                'echo "ALTACLI_EXIT=$?"')

    def _command(self, repo: str, model_ref: str, settings: list[str], budget_sec: int | None = None, resume: bool = False,
                 prompt_file: str = f"{_RUN_DIR}/instruction.md", jsonl: str = _JSONL, stderr: str = _STDERR) -> str:
        command = super()._command(repo, model_ref, settings, budget_sec, resume, prompt_file, jsonl, stderr)
        if self._adaptive is None:
            return command
        # CodeAlta's Adaptive chooses the model: the level is given by the settings, the session by --tier.
        level = self._adaptive
        adaptive = [
            "--tier", f"adaptive:{level}",
            "-c", f"models.adaptive.level={_toml_string(level)}",
            "-c", f"models.adaptive.{level}.model={_toml_string(model_ref)}",
            "-c", f"models.adaptive.{level}.critics=[{', '.join(_toml_string(critic) for critic in self._critics)}]",
        ]
        model = f"-m {shlex.quote(model_ref)}"
        if model not in command:
            raise RuntimeError(f"the command names no model to replace: {command}")
        return command.replace(model, " ".join(shlex.quote(part) for part in adaptive), 1)

    async def _upload_text(self, environment: BaseEnvironment, text: str, name: str) -> str:
        with tempfile.TemporaryDirectory(prefix="codealta-fusion-") as tmp:
            local = Path(tmp) / name
            local.write_text(text, encoding="utf-8")
            await environment.upload_file(local, f"{_RUN_DIR}/{name}")
        await environment.exec(command=f"chmod a+r {_RUN_DIR}/{name}", user="root", timeout_sec=60)
        return f"{_RUN_DIR}/{name}"

    async def _last_result(self, environment: BaseEnvironment, jsonl: str) -> dict[str, Any] | None:
        r = await environment.exec(command=f"tail -n 1 {jsonl} 2>/dev/null; true", timeout_sec=120)
        lines = [line for line in (r.stdout or "").splitlines() if line.strip()]
        return self._parse(lines[-1], "_codealta/result") if lines else None

    async def _helper(self, environment: BaseEnvironment, repo: str, role: str, index: int, model_ref_text: str,
                      prompt: str, settings: list[str], env: dict[str, str], budget_sec: int, read_only: bool | str
                      ) -> tuple[dict[str, Any] | None, int | None]:
        provider, model, effort = parse_model_ref(model_ref_text)
        model_ref = f"{provider}:{model}" + (f"@{effort}" if effort else "")
        prompt_file = await self._upload_text(environment, prompt, f"{role}-{index}.md")
        jsonl, stderr = f"{_LOGS}/altacli.{role}-{index}.jsonl", f"{_LOGS}/altacli.{role}-{index}.stderr"
        command = self._helper_command(repo, model_ref, settings, budget_sec, str(uuid.uuid4()), prompt_file, jsonl, stderr, read_only)
        self.logger.info(f"altacli {role} {index}: {command}")
        result = await environment.exec(command=command, env=env, timeout_sec=budget_sec)
        exit_code = self._exit_code(result.stdout)
        return await self._last_result(environment, jsonl), exit_code

    # ----- run -----

    async def run(self, instruction: str, environment: BaseEnvironment, context: AgentContext) -> None:
        instruction = self.render_instruction(instruction)
        self._legs = []
        try:
            model_ref, settings, env = self._multi_settings()
            env.update({"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
            r = await environment.exec(
                # A task without a repository (Terminal-Bench) works in the WORKDIR of its image, which may not be /app.
                command=(f"mkdir -p {_RUN_DIR} {_LOGS} && repo=$({_FIND_REPO}) && {{ [ -d \"$repo\" ] || repo=$(pwd); }} && "
                         f"echo \"$repo\" > {_RUN_DIR}/repo && "
                         f"git -c safe.directory='*' -C \"$repo\" rev-parse HEAD > {_RUN_DIR}/base 2>/dev/null; "
                         f"chmod -R a+rwX {_RUN_DIR}; cat {_RUN_DIR}/repo"),
                user="root", timeout_sec=120)
            repo = (r.stdout or "").strip().splitlines()[-1] if (r.stdout or "").strip() else "/app"
            await self._upload_text(environment, instruction, "instruction.md")

            command = self._command(repo, model_ref, settings)
            self.logger.info(f"altacli primary: {command}")
            started = time.monotonic()
            result = await environment.exec(command=command, env=env, timeout_sec=self._budget_sec)
            exit_code = self._exit_code(result.stdout)
            await self._read_result(environment, context, exit_code)
            self._primary_result = self._result
            self._legs.append(_leg_record("primary", model_ref, self._result, exit_code,
                                          wall_sec=round(time.monotonic() - started)))

            for step in self._steps:
                if exit_code not in (0, 1):
                    break
                if step == "recheck":
                    first = self._result
                    exit_code, _ = await self._recheck_run(environment, context, repo, model_ref, settings, env, exit_code,
                                                           self._helper_sec)
                    if (context.metadata or {}).get("codealta_recheck", {}).get("rechecked"):
                        second = await self._last_result(environment, f"{_LOGS}/altacli.recheck.jsonl")
                        self._legs.append(_leg_record("recheck", model_ref, second, exit_code))
                    self._result = first
                elif step == "critique":
                    exit_code = await self._critique(environment, repo, instruction, model_ref, settings, env, exit_code)
                elif step == "cascade":
                    exit_code = await self._cascade(environment, repo, instruction, settings, env, exit_code)

            if exit_code not in (0, 4, 124, 137):
                failure = (self._result or {}).get("failure") or {}
                tail = await environment.exec(command=f"tail -c 4000 {_STDERR} 2>/dev/null; true", timeout_sec=60)
                detail = SimpleNamespace(return_code=exit_code, stdout=json.dumps(failure) if failure else "", stderr=tail.stdout or "")
                raise self._classify_exec_error("altacli run", detail)
        finally:
            self._record(context)
            try:
                r = await environment.exec(command=_CAPTURE, user="root", timeout_sec=300)
                self.logger.info(f"model.patch bytes: {(r.stdout or '').strip()}")
            except Exception as e:  # never mask the trial outcome
                self.logger.warning(f"patch capture failed: {e}")

    async def _is_git(self, environment: BaseEnvironment) -> bool:
        r = await environment.exec(command=f"repo=$(cat {_RUN_DIR}/repo); git -c safe.directory='*' -C \"$repo\" rev-parse --is-inside-work-tree 2>/dev/null; true",
                                   user="root", timeout_sec=60)
        return "true" in (r.stdout or "")

    async def _review_prompt(self, environment: BaseEnvironment, instruction: str, repo: str, git: bool,
                             judge: bool) -> tuple[str, bool | str]:
        """The prompt of a critic or a judge, and its tools: none for a review of the diff alone."""
        if not git:
            return state_prompt(instruction, repo, self._report(), judge), True
        if self._critic_mode == "diff":
            r = await environment.exec(command=f"cat {_RUN_DIR}/draft.patch 2>/dev/null; true", user="root", timeout_sec=120)
            return diff_prompt(instruction, r.stdout or "", judge), "none"
        return critic_prompt(instruction, repo, judge), True

    def _report(self) -> str | None:
        return (self._primary_result or {}).get("finalMessage")

    async def _changed_snapshot(self, environment: BaseEnvironment) -> bool:
        r = await environment.exec(command=_CHANGED, user="root", timeout_sec=120)
        if "CHANGED=1" not in (r.stdout or ""):
            return False
        r = await environment.exec(command=_SNAPSHOT, user="root", timeout_sec=300)
        return r.return_code == 0

    async def _restore(self, environment: BaseEnvironment) -> bool:
        r = await environment.exec(command=_RESTORE, user="root", timeout_sec=300)
        return "RESTORED=1" in (r.stdout or "")

    async def _critique(self, environment: BaseEnvironment, repo: str, instruction: str, model_ref: str,
                        settings: list[str], env: dict[str, str], exit_code: int | None) -> int | None:
        git = await self._is_git(environment)
        if git and not await self._changed_snapshot(environment):
            self._legs.append({"role": "critique", "skipped": "no change"})
            return exit_code
        prompt, access = await self._review_prompt(environment, instruction, repo, git, judge=False)
        reviews = await asyncio.gather(*(
            self._helper(environment, repo, "critic", index, critic, prompt, settings, env, self._helper_sec, read_only=access)
            for index, critic in enumerate(self._critics)))
        undone = await self._restore(environment) if git else None
        findings: list[str] = []
        for critic, (result, code) in zip(self._critics, reviews):
            verdict, text = parse_verdict((result or {}).get("finalMessage"))
            self._legs.append(_leg_record("critic", critic, result, code, verdict=verdict, undone=undone, findings=text[:2000]))
            if verdict == "REVISE" and text:
                findings.append(text)
        if not findings:
            return exit_code
        prompt_file = await self._upload_text(environment, revision_prompt(findings), "revision.md")
        command = self._command(repo, model_ref, settings, budget_sec=self._helper_sec, resume=True, prompt_file=prompt_file,
                                jsonl=f"{_LOGS}/altacli.revision.jsonl", stderr=f"{_LOGS}/altacli.revision.stderr")
        self.logger.info(f"altacli revision: {command}")
        result = await environment.exec(command=command, env=env, timeout_sec=self._helper_sec)
        code = self._exit_code(result.stdout)
        self._legs.append(_leg_record("revision", model_ref, await self._last_result(environment, f"{_LOGS}/altacli.revision.jsonl"), code))
        return code

    async def _cascade(self, environment: BaseEnvironment, repo: str, instruction: str, settings: list[str],
                       env: dict[str, str], exit_code: int | None) -> int | None:
        assert self._judge and self._repair
        git = await self._is_git(environment)
        if not git or await self._changed_snapshot(environment):
            prompt, access = await self._review_prompt(environment, instruction, repo, git, judge=True)
            result, code = await self._helper(environment, repo, "judge", 0, self._judge, prompt,
                                              settings, env, self._helper_sec, read_only=access)
            undone = await self._restore(environment) if git else None
            verdict, reasons = parse_verdict((result or {}).get("finalMessage"))
            self._legs.append(_leg_record("judge", self._judge, result, code, verdict=verdict, undone=undone, findings=reasons[:2000]))
            if verdict != "ESCALATE":
                return exit_code
        else:
            # Nothing to judge: the primary did not change the repository, which the repair takes over.
            await environment.exec(command=f": > {_RUN_DIR}/draft.patch; chmod a+r {_RUN_DIR}/draft.patch", user="root", timeout_sec=60)
            reasons = "The first attempt changed nothing in the repository."
        prompt = repair_prompt(instruction, reasons) if git else state_repair_prompt(instruction, reasons)
        result, code = await self._helper(environment, repo, "repair", 0, self._repair, prompt,
                                          settings, env, max(self._budget_sec, _HELPER_MIN_SEC), read_only=False)
        self._legs.append(_leg_record("repair", self._repair, result, code))
        return code

    def _record(self, context: AgentContext) -> None:
        metadata: dict[str, Any] = dict(context.metadata or {})
        total = sum(leg["cost_usd"] for leg in self._legs if isinstance(leg.get("cost_usd"), (int, float)))
        metadata["fusion"] = {"steps": self._steps, "critics": self._critics, "judge": self._judge, "repair": self._repair,
                              "legs": self._legs, "total_cost_usd": total}
        if self._legs:
            metadata["codealta_estimated_cost_usd"] = total
        context.metadata = metadata
