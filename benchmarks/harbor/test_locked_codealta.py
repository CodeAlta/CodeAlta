from __future__ import annotations

import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from harbor.models.agent.context import AgentContext

from locked_codealta import (
    LockedCodeAlta,
    _activation_command,
    _claude_code_install_command,
    _claude_code_model,
    _combine,
    _runtime_from_probe,
    _trouble,
)


def _agent(model: str, **kwargs) -> LockedCodeAlta:
    return LockedCodeAlta(logs_dir=Path(tempfile.mkdtemp(prefix="codealta-test-")), model_name=model, **kwargs)


class RuntimeSelectionTests(unittest.TestCase):
    def test_runtime_from_probe_selects_musl(self) -> None:
        self.assertEqual("linux-musl-x64", _runtime_from_probe("linux-musl-x64\n"))

    def test_runtime_from_probe_selects_glibc(self) -> None:
        self.assertEqual("linux-x64", _runtime_from_probe("probe output\nlinux-x64\n"))

    def test_runtime_from_probe_rejects_an_unknown_runtime(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "unsupported libc runtime"):
            _runtime_from_probe("linux-arm64\n")

    def test_activation_command_selects_the_runtime_and_keeps_legacy_fallback(self) -> None:
        command = _activation_command("linux-musl-x64")

        self.assertIn("selected=/opt/codealta/package/linux-musl-x64", command)
        self.assertIn("selected=/opt/codealta/package", command)
        self.assertIn('[ ! -f "$selected/altacli" ]', command)
        self.assertNotIn('[ ! -x "$selected/altacli" ]', command)
        self.assertIn('"$selected/altacli"', command)
        self.assertIn('exec \\"$selected/altacli\\" \\"\\$@\\"', command)


class ClaudeCodeTests(unittest.TestCase):
    def test_a_claude_model_id_becomes_the_alias_the_provider_offers(self) -> None:
        self.assertEqual("opus", _claude_code_model("claude-opus-5-5"))
        self.assertEqual("sonnet", _claude_code_model("claude-sonnet-5-5"))
        self.assertEqual("opus[1m]", _claude_code_model("opus[1m]"))

    def test_the_pinned_cli_is_downloaded_and_checked_with_the_installer_as_fallback(self) -> None:
        command = _claude_code_install_command("linux-musl-x64", "2.1.309")

        self.assertIn("releases/download/v2.1.309/claude-linux-x64-musl.tar.gz", command)
        self.assertIn("51a0657730ec280a43fee47650fe1c412b07ec425ce496f133bac4bcb81d5e05  claude-code.tar.gz", command)
        self.assertIn("bootstrap.sh | bash -s -- 2.1.309", command)
        self.assertIn("/opt/claude-code/claude --version", command)

    def test_another_version_uses_the_installer_alone(self) -> None:
        command = _claude_code_install_command("linux-x64", "2.2.0")

        self.assertNotIn("releases/download", command)
        self.assertIn("bash -s -- 2.2.0", command)

    def test_settings_run_the_installed_cli_with_the_key_of_the_environment(self) -> None:
        agent = _agent("anthropic/claude-opus-5-5", provider_type="claude-code", reasoning_effort="xhigh")
        with mock.patch.dict(os.environ, {"ANTHROPIC_API_KEY": "sk-test"}):
            model_ref, settings, key_name, key_value = agent._model_settings()

        self.assertTrue(agent._uses_claude_code())
        self.assertEqual("anthropic:opus@xhigh", model_ref)
        self.assertEqual(("ANTHROPIC_API_KEY", "sk-test"), (key_name, key_value))
        self.assertIn('providers.anthropic.type="claude-code"', settings)
        self.assertIn('providers.anthropic.command="/opt/claude-code/claude"', settings)
        self.assertIn('providers.anthropic.anthropic_api_key="use"', settings)
        self.assertFalse(any("api_key_env" in setting for setting in settings), "the CLI reads ANTHROPIC_API_KEY itself")

    def test_the_own_harness_keeps_its_provider(self) -> None:
        agent = _agent("anthropic/claude-opus-5-5")
        with mock.patch.dict(os.environ, {"ANTHROPIC_API_KEY": "sk-test"}):
            model_ref, settings, _, _ = agent._model_settings()

        self.assertFalse(agent._uses_claude_code())
        self.assertEqual("anthropic:claude-opus-5-5", model_ref)
        self.assertIn('providers.anthropic.type="anthropic"', settings)
        self.assertIn('providers.anthropic.api_key_env="ANTHROPIC_API_KEY"', settings)


class ResultTests(unittest.TestCase):
    def test_the_session_header_records_the_provider_type_and_an_estimated_cost_stays_out_of_cost_usd(self) -> None:
        agent = _agent("openai/gpt-6-astra")
        context = AgentContext()
        header = {"sessionId": "s", "provider": "openai", "providerType": "openai-responses", "model": "gpt-6-astra"}
        result = {"status": "completed", "usage": {"inputTokens": 10, "outputTokens": 2},
                  "cost": {"amount": 1.5, "currency": "USD", "_meta": {"codealta": {"estimated": True}}}}

        agent._fill(context, header, result, 0)

        self.assertEqual("openai-responses", context.metadata["codealta_session"]["providerType"])
        self.assertIsNone(context.cost_usd)
        self.assertEqual(1.5, context.metadata["codealta_estimated_cost_usd"])

    def test_a_reported_cost_is_cost_usd(self) -> None:
        agent = _agent("anthropic/claude-opus-5-5", provider_type="claude-code")
        context = AgentContext()

        agent._fill(context, {"providerType": "claude-code"}, {"cost": json.loads('{"amount": 2.25, "currency": "USD"}')}, 0)

        self.assertEqual(2.25, context.cost_usd)
        self.assertNotIn("codealta_estimated_cost_usd", context.metadata)


def _line(method: str, **params) -> str:
    return json.dumps({"jsonrpc": "2.0", "method": method, "params": params})


def _call(call: str, title: str) -> str:
    return _line("session/update", sessionId="root", update={"sessionUpdate": "tool_call", "toolCallId": call, "title": title})


def _output(call: str, text: str) -> str:
    return _line("session/update", sessionId="root",
                 update={"sessionUpdate": "tool_call_update", "toolCallId": call, "content": [{"type": "content", "content": {"type": "text", "text": text}}]})


def _result(status: str = "completed", answer: str = "Done.") -> str:
    return _line("_codealta/result", sessionId="root", status=status, finalMessage=answer)


class EscalationTests(unittest.TestCase):
    header = _line("_codealta/session", sessionId="root")

    def test_a_run_whose_last_test_run_fails_is_in_trouble(self) -> None:
        lines = [self.header, _call("t1", "shell_command python -m pytest tests/"), _output("t1", "exit_code: 1\nFAILED test_a"),
                 _call("e1", "apply_patch src/a.py"), _result()]

        self.assertEqual("the last test run failed (exit code 1).", _trouble(lines))

    def test_a_later_passing_test_run_clears_it(self) -> None:
        lines = [self.header, _call("t1", "shell_command go test ./..."), _output("t1", "exit_code: 1"),
                 _call("t2", "shell_command go test ./..."), _output("t2", "exit_code: 0"), _result()]

        self.assertIsNone(_trouble(lines))

    def test_no_answer_and_a_refusal_are_trouble_and_a_timeout_is_not(self) -> None:
        self.assertEqual("the turn ended without an answer.", _trouble([self.header, _result("failed", "")]))
        self.assertIn("tools cannot be used", _trouble([self.header, _result(answer="I cannot read the files of the project.")]))
        self.assertIsNone(_trouble([self.header, _result("timeout", "")]))
        self.assertIsNone(_trouble([self.header]), "a run killed before its result is not continued")

    def test_the_tests_of_a_sub_agent_do_not_count(self) -> None:
        child = _line("session/update", sessionId="child", update={"sessionUpdate": "tool_call", "toolCallId": "c1", "title": "shell_command npm test"})
        child_output = _line("session/update", sessionId="child",
                             update={"sessionUpdate": "tool_call_update", "toolCallId": "c1", "content": [{"type": "content", "content": {"type": "text", "text": "exit_code: 1"}}]})

        self.assertIsNone(_trouble([self.header, child, child_output, _result()]))

    def test_the_results_of_a_run_and_its_escalation_add_up(self) -> None:
        first = {"status": "completed", "usage": {"inputTokens": 100, "outputTokens": 10}, "cost": {"amount": 0.5, "currency": "USD"}, "durationMs": 1000}
        second = {"status": "completed", "usage": {"inputTokens": 50, "outputTokens": 5, "cachedReadTokens": 40}, "cost": {"amount": 1.0, "currency": "USD"}, "durationMs": 500}

        combined = _combine(first, second)

        self.assertEqual({"inputTokens": 150, "outputTokens": 15, "cachedReadTokens": 40}, combined["usage"])
        self.assertEqual(1.5, combined["cost"]["amount"])
        self.assertEqual(1500, combined["durationMs"])
        self.assertIs(first, _combine(first, None))

    def test_an_escalation_keeps_the_session_and_resumes_it_on_the_stronger_model(self) -> None:
        agent = _agent("anthropic/claude-haiku-5-5", escalate_to="claude-opus-5-5", escalate_effort="high")
        first = agent._command("/app", "anthropic:claude-haiku-5-5", [])
        second = agent._command("/app", "anthropic:claude-opus-5-5@high", [], budget_sec=1200, resume=True,
                                prompt_file="/tmp/codealta-run/continue.md", jsonl="/logs/agent/altacli.escalation.jsonl")

        self.assertNotIn("--ephemeral", first)
        self.assertIn(f"--session-id={agent._session_id}", first)
        self.assertIn(f"--resume={agent._session_id}", second)
        self.assertIn("< /tmp/codealta-run/continue.md > /logs/agent/altacli.escalation.jsonl", second)
        self.assertIn("--timeout 1110", second)

    def test_without_an_escalation_the_run_stays_ephemeral(self) -> None:
        command = _agent("anthropic/claude-opus-5-5")._command("/app", "anthropic:claude-opus-5-5", [])

        self.assertIn("--ephemeral", command)
        self.assertNotIn("--session-id", command)


if __name__ == "__main__":
    unittest.main()
