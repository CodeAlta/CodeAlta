"""Regression checks for the release package inventory. Run with python -m unittest discover -s this-folder."""

import unittest
import xml.etree.ElementTree as ET

import release


class PackageInventoryTests(unittest.TestCase):
    def test_every_packable_tool_and_runtime_is_checked(self):
        expected = set()
        for project in (release.ROOT / "src").glob("*/*.csproj"):
            root = ET.parse(project).getroot()
            if root.findtext(".//PackAsTool") != "true" or root.findtext(".//IsPackable") != "true":
                continue
            package = root.findtext(".//PackageId").lower()
            expected.add(package)
            expected.update(
                f"{package}.{runtime.strip()}"
                for runtime in root.findtext(".//RuntimeIdentifiers", "").split(";")
                if runtime.strip()
            )

        actual = release.package_ids()
        self.assertEqual(expected, set(actual))
        self.assertEqual(len(actual), len(set(actual)))


if __name__ == "__main__":
    unittest.main()
