from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from fusion_codealta import FusionCodeAlta, critic_prompt, parse_model_ref, parse_verdict, revision_prompt, state_prompt


def _agent(model: str = "openai/gpt-6.1-sol", **kwargs) -> FusionCodeAlta:
    return FusionCodeAlta(logs_dir=Path(tempfile.mkdtemp(prefix="codealta-fusion-test-")), model_name=model, **kwargs)


class ParsingTests(unittest.TestCase):
    def test_a_model_reference_has_an_optional_effort(self) -> None:
        self.assertEqual(("anthropic", "claude-opus-5-5", "high"), parse_model_ref("anthropic:claude-opus-5-5@high"))
        self.assertEqual(("openai", "gpt-6-luna", None), parse_model_ref("OpenAI:gpt-6-luna"))
        with self.assertRaises(ValueError):
            parse_model_ref("claude-opus-5-5")
        with self.assertRaises(ValueError):
            parse_model_ref("anthropic:claude-opus-5-5@huge")

    def test_the_last_verdict_line_wins_and_the_findings_are_what_precedes_it(self) -> None:
        verdict, findings = parse_verdict("1. `parse` drops the last field (src/csv.js:42).\n\nVERDICT: REVISE")
        self.assertEqual("REVISE", verdict)
        self.assertEqual("1. `parse` drops the last field (src/csv.js:42).", findings)
        self.assertEqual("APPROVE", parse_verdict("VERDICT: REVISE was considered.\n**VERDICT: APPROVE**")[0])
        self.assertEqual("ESCALATE", parse_verdict("verdict: escalate")[0])

    def test_an_answer_without_a_verdict_gives_none(self) -> None:
        self.assertEqual((None, "Looks fine."), parse_verdict("Looks fine."))
        self.assertEqual((None, ""), parse_verdict(None))


class PromptTests(unittest.TestCase):
    def test_the_critic_prompt_holds_the_request_the_diff_and_the_verdicts(self) -> None:
        prompt = critic_prompt("Fix the parser.", "/app", judge=False)
        self.assertIn("Fix the parser.", prompt)
        self.assertIn("/tmp/codealta-run/draft.patch", prompt)
        self.assertIn("VERDICT: REVISE", prompt)
        self.assertIn("VERDICT: ESCALATE", critic_prompt("x", "/app", judge=True))

    def test_the_state_prompt_holds_the_report_of_the_engineer(self) -> None:
        prompt = state_prompt("Create /app/ssl/server.key.", "/app", "Created the key.", judge=False)
        self.assertIn("Created the key.", prompt)
        self.assertIn("do not change the state of the machine", prompt)

    def test_the_revision_prompt_gives_every_finding(self) -> None:
        prompt = revision_prompt(["first finding", "second finding"])
        self.assertIn("first finding", prompt)
        self.assertIn("second finding", prompt)


class SettingsTests(unittest.TestCase):
    def test_every_provider_of_the_legs_is_configured_with_its_key(self) -> None:
        with mock.patch.dict(os.environ, {"OPENAI_API_KEY": "o", "ANTHROPIC_API_KEY": "a"}):
            agent = _agent(reasoning_effort="medium", steps="critique", critics="anthropic:claude-opus-5-5@high")
            model_ref, settings, env = agent._multi_settings()
        self.assertEqual("openai:gpt-6.1-sol@medium", model_ref)
        self.assertIn('providers.anthropic.type="anthropic"', settings)
        self.assertIn('providers.anthropic.api_key_env="ANTHROPIC_API_KEY"', settings)
        self.assertEqual({"OPENAI_API_KEY": "o", "ANTHROPIC_API_KEY": "a"}, env)

    def test_a_helper_is_read_only_on_its_own_provider_and_profile(self) -> None:
        with mock.patch.dict(os.environ, {"OPENAI_API_KEY": "o", "ANTHROPIC_API_KEY": "a"}):
            agent = _agent(steps="critique", critics="anthropic:claude-opus-5-5@high")
            _, settings, _ = agent._multi_settings()
        command = agent._helper_command("/app", "anthropic:claude-opus-5-5@high", settings, 360, "0123456789", "/tmp/p.md",
                                        "/logs/agent/c.jsonl", "/logs/agent/c.stderr", read_only=True)
        self.assertIn("--disable-tools web,write_file,replace_in_file,apply_patch", command)
        self.assertIn("--profile /tmp/codealta-profile-01234567", command)
        self.assertIn("chat.default_provider=\"anthropic\"", command)
        self.assertNotIn("chat.default_provider=\"openai\"", command)

    def test_steps_need_their_models(self) -> None:
        with self.assertRaisesRegex(ValueError, "critics="):
            _agent(steps="critique")
        with self.assertRaisesRegex(ValueError, "judge= and repair="):
            _agent(steps="cascade", judge="anthropic:claude-sonnet-5-5")
        with self.assertRaisesRegex(ValueError, "unknown steps"):
            _agent(steps="vote")


if __name__ == "__main__":
    unittest.main()
