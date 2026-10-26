from __future__ import annotations

import unittest

from locked_codealta import _activation_command, _runtime_from_probe


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


if __name__ == "__main__":
    unittest.main()
