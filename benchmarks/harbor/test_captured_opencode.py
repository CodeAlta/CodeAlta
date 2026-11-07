from __future__ import annotations

import asyncio
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from harbor.agents.installed.opencode import OpenCode
from harbor.models.agent.context import AgentContext

from captured_opencode import CapturedOpenCode
from locked_codealta import _CAPTURE


class _Environment:
    """Records the commands an agent runs, in their order."""

    def __init__(self) -> None:
        self.commands: list[str] = []

    async def exec(self, command: str, **_: object) -> SimpleNamespace:
        self.commands.append(command)
        return SimpleNamespace(stdout="128\n", stderr="", return_code=0)


def _agent() -> CapturedOpenCode:
    return CapturedOpenCode(logs_dir=Path(tempfile.mkdtemp(prefix="opencode-test-")), model_name="deepseek/deepseek-flash")


class CaptureTests(unittest.TestCase):
    def test_the_base_is_recorded_before_opencode_runs_and_the_patch_captured_after(self) -> None:
        environment = _Environment()

        async def opencode_run(agent, instruction, env, context) -> None:
            env.commands.append("opencode run")

        with mock.patch.object(OpenCode, "run", opencode_run):
            asyncio.run(_agent().run("Fix the bug.", environment, AgentContext()))

        self.assertEqual(3, len(environment.commands))
        self.assertIn("rev-parse HEAD > /tmp/codealta-run/base", environment.commands[0])
        self.assertEqual("opencode run", environment.commands[1])
        self.assertEqual(_CAPTURE, environment.commands[2])
        self.assertIn("/logs/agent/model.patch", environment.commands[2])

    def test_a_run_that_fails_still_has_its_patch_captured(self) -> None:
        environment = _Environment()

        async def opencode_run(agent, instruction, env, context) -> None:
            raise RuntimeError("OpenCode emitted error event(s)")

        with mock.patch.object(OpenCode, "run", opencode_run):
            with self.assertRaisesRegex(RuntimeError, "error event"):
                asyncio.run(_agent().run("Fix the bug.", environment, AgentContext()))

        self.assertEqual(_CAPTURE, environment.commands[-1])


if __name__ == "__main__":
    unittest.main()
