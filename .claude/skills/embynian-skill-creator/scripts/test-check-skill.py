#!/usr/bin/env python3
"""单行 frontmatter 的离线回归；只用标准库和内存夹具，不读取真实设置。"""

import importlib.util
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("check-skill.py")
SPEC = importlib.util.spec_from_file_location("check_skill", SCRIPT)
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)


class FrontmatterTests(unittest.TestCase):
    def check_description(self, raw: str):
        report = CHECK.Report("example")
        fields = CHECK.parse_frontmatter(f'name: "example"\ndescription: {raw}', report)
        CHECK.check_frontmatter(fields, Path("example"), report)
        return fields, report

    def test_valid_strings(self):
        cases = [
            ('"Use this skill when reviewing repository workflows."',
             "Use this skill when reviewing repository workflows."),
            (r'"Review \"quoted\" names and C:\\Example paths in this skill."',
             'Review "quoted" names and C:\\Example paths in this skill.'),
            ("'Review the repository''s paths such as C:\\Example safely.'",
             "Review the repository's paths such as C:\\Example safely."),
            ('"Use this skill when reviewing repository workflows." # comment',
             "Use this skill when reviewing repository workflows."),
            ("Use https://example.invalid/docs for repository workflow review. # comment",
             "Use https://example.invalid/docs for repository workflow review."),
            (r'"Review \x41, \u4e2d and \U0001F600 in repository workflows."',
             "Review A, 中 and 😀 in repository workflows."),
            ('"Review \\\t separators in repository workflows safely."',
             "Review \t separators in repository workflows safely."),
            ("Use this skill when:\u00a0reviewing repository workflows.",
             "Use this skill when:\u00a0reviewing repository workflows."),
        ]
        for raw, expected in cases:
            with self.subTest(raw=raw):
                fields, report = self.check_description(raw)
                self.assertEqual([], report.errors)
                self.assertEqual([], report.warnings)
                self.assertEqual(expected, fields["description"])

    def test_invalid_strings_fail(self):
        cases = [
            '"Use this skill when reviewing repository workflows.',
            "'Use this skill when reviewing repository workflows.",
            '"Use this skill when reviewing repository workflows." junk',
            r'"Use this skill with C:\Users\Example when reviewing workflows."',
            r'"Use this skill with \q when reviewing repository workflows."',
            r'"Use this skill with \u12 when reviewing repository workflows."',
            r'"Use this skill with \U00110000 in repository workflows."',
            "Use this skill when: reviewing repository workflows.",
            "Use this skill when:\treviewing repository workflows.",
            "Use this skill when reviewing repository workflows:",
        ]
        for raw in cases:
            with self.subTest(raw=raw):
                _, report = self.check_description(raw)
                self.assertFalse(report.ok)
                self.assertTrue(any("第 3 行" in error for error in report.errors))

    def test_escaped_forbidden_character_is_checked_after_decoding(self):
        _, report = self.check_description(
            r'"Use this skill when reviewing \u003cworkflow\u003e changes."')
        self.assertTrue(any("尖括号" in error for error in report.errors))

    def test_key_requires_separation_after_colon(self):
        report = CHECK.Report("example")
        fields = CHECK.parse_frontmatter(
            'name: "example"\ndescription:"Use this skill when reviewing repository workflows."', report)
        CHECK.check_frontmatter(fields, Path("example"), report)
        self.assertFalse(report.ok)

    def test_flow_metadata_is_explicitly_unchecked(self):
        report = CHECK.Report("example")
        fields = CHECK.parse_frontmatter("metadata: {agent_created: true}", report)
        self.assertEqual("{agent_created: true}", fields["metadata"])
        self.assertEqual([], report.errors)
        self.assertTrue(any("metadata" in warning and "未校验" in warning for warning in report.warnings))


if __name__ == "__main__":
    unittest.main()
