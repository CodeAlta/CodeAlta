"""Harbor's ``opencode`` agent with the patch capture of the locked adapters, for ``patch_replay:PatchReplayAgent``.

Harbor's agent keeps OpenCode's output (``opencode.txt``, ``trajectory.json``) but not its change: a replay of its job
applied nothing. This agent records the repository and its base commit before OpenCode runs, then writes the diff of
the agent's work against that commit to /logs/agent/model.patch, as ``locked_codealta.py`` does for CodeAlta, also
when the run fails or ends by its budget.

Usage (see ../swe-bench-pro/README.md)::

    PYTHONPATH=v2/tooling:<codealta>/benchmarks/harbor harbor run -p <dataset> -e docker \
      -a captured_opencode:CapturedOpenCode -m deepseek/deepseek-flash --ak version=1.19.5 ...
"""
from __future__ import annotations

from harbor.agents.installed.opencode import OpenCode
from harbor.environments.base import BaseEnvironment
from harbor.models.agent.context import AgentContext

from locked_codealta import _CAPTURE, _FIND_REPO, _LOGS, _RUN_DIR

# The repository and the commit the task starts from, before the agent changes anything: what _CAPTURE diffs against.
_RECORD_BASE = (
    f"mkdir -p {_RUN_DIR} {_LOGS} && repo=$({_FIND_REPO}) && echo \"$repo\" > {_RUN_DIR}/repo && "
    f"git -c safe.directory='*' -C \"$repo\" rev-parse HEAD > {_RUN_DIR}/base 2>/dev/null; "
    f"chmod -R a+rwX {_RUN_DIR}; cat {_RUN_DIR}/repo"
)


class CapturedOpenCode(OpenCode):
    """``opencode run`` as Harbor runs it, then the capture of its patch."""

    async def run(self, instruction: str, environment: BaseEnvironment, context: AgentContext) -> None:
        await environment.exec(command=_RECORD_BASE, user="root", timeout_sec=120)
        try:
            await super().run(instruction, environment, context)
        finally:
            try:
                result = await environment.exec(command=_CAPTURE, user="root", timeout_sec=300)
                self.logger.info(f"model.patch bytes: {(result.stdout or '').strip()}")
            except Exception as error:  # never mask the trial outcome
                self.logger.warning(f"patch capture failed: {error}")
