"""Run the validator in child processes with entirely controlled synthetic key values."""

import base64
import os
from pathlib import Path
import subprocess
import sys
import unittest


VALIDATOR = Path(__file__).with_name("validate_gdk_content_keys.py")
RELEASE = "PCL_BEDROCK_RELEASE_CIK"
PREVIEW = "PCL_BEDROCK_PREVIEW_CIK"
# Synthetic format fixtures only. Never invoke a main-project build with these values.
SYNTHETIC_HEX = "aB" * 48


class KeyConfigurationTests(unittest.TestCase):
    child_process_cases = 0

    def run_validator(self, overrides=None, remove=()):
        # Do not copy, inspect, preserve, or restore the parent's content-key environment.
        environment = {
            "PCL_WRITE_SECRET": "1",
            RELEASE: SYNTHETIC_HEX,
            PREVIEW: SYNTHETIC_HEX,
            "PYTHONUTF8": "1",
        }
        if os.name == "nt":
            # Windows needs this non-secret OS setting when creating a child process.
            environment["SystemRoot"] = os.environ.get("SystemRoot", r"C:\Windows")
        environment.update(overrides or {})
        for variable in remove:
            environment.pop(variable, None)
        type(self).child_process_cases += 1
        result = subprocess.run(
            [sys.executable, str(VALIDATOR)],
            env=environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=10,
            check=False,
        )
        output = result.stdout + result.stderr
        for variable in (RELEASE, PREVIEW):
            value = environment.get(variable)
            if value:
                self.assertNotIn(value, output, "Validator printed a supplied synthetic key value.")
        return result.returncode, output

    def test_accepts_minimum_length_and_mixed_case(self):
        exit_code, output = self.run_validator()
        self.assertEqual(0, exit_code)
        self.assertIn("passed format checks", output)

    def test_accepts_values_longer_than_48_bytes(self):
        exit_code, _ = self.run_validator({RELEASE: "aB" * 49, PREVIEW: "CD" * 64})
        self.assertEqual(0, exit_code)

    def test_checks_injection_flag_presence(self):
        exit_code, output = self.run_validator(remove=("PCL_WRITE_SECRET",))
        self.assertEqual(1, exit_code)
        self.assertIn("PCL_WRITE_SECRET:", output)
        self.assertNotIn(RELEASE + ":", output)
        self.assertNotIn(PREVIEW + ":", output)

    def test_matches_source_generator_presence_contract(self):
        # The generator uses Contains, so present empty/zero values still enable injection.
        for value in ("", "0"):
            with self.subTest(flag=value):
                self.assertEqual(0, self.run_validator({"PCL_WRITE_SECRET": value})[0])

    def test_reports_both_missing_content_keys(self):
        exit_code, output = self.run_validator(remove=(RELEASE, PREVIEW))
        self.assertEqual(1, exit_code)
        self.assertIn(RELEASE + ":", output)
        self.assertIn(PREVIEW + ":", output)

    def test_checks_missing_channels_independently(self):
        for missing, valid in ((RELEASE, PREVIEW), (PREVIEW, RELEASE)):
            with self.subTest(channel=missing):
                exit_code, output = self.run_validator(remove=(missing,))
                self.assertEqual(1, exit_code)
                self.assertIn(missing + ":", output)
                self.assertNotIn(valid + ":", output)

    def test_rejects_invalid_formats_for_either_channel(self):
        invalid = {
            "empty": "",
            "non_hex": "gH" * 48,
            "odd_below_minimum": "a" * 95,
            "odd_above_minimum": "a" * 97,
            "short_even": "aB" * 47,
            "prefix": "0x" + SYNTHETIC_HEX,
            "quotes": '"' + SYNTHETIC_HEX + '"',
            "leading_space": " " + SYNTHETIC_HEX,
            "trailing_space": SYNTHETIC_HEX + " ",
            "trailing_newline": SYNTHETIC_HEX + "\n",
            "separators": ":".join(["aB"] * 48),
            "base64": base64.b64encode(bytes(48)).decode("ascii"),
            "unicode": "ＡＢ" * 48,
            "workflow_annotation": "::error::SYNTHETIC_INPUT_MUST_STAY_PRIVATE",
        }
        for channel, other in ((RELEASE, PREVIEW), (PREVIEW, RELEASE)):
            for case, value in invalid.items():
                with self.subTest(channel=channel, case=case):
                    exit_code, output = self.run_validator({channel: value})
                    self.assertEqual(1, exit_code)
                    self.assertIn(channel + ":", output)
                    self.assertNotIn(other + ":", output)

    def test_reports_all_invalid_configuration_without_values(self):
        exit_code, output = self.run_validator(
            {RELEASE: "SYNTHETIC_BAD_RELEASE", PREVIEW: "SYNTHETIC_BAD_PREVIEW"},
            remove=("PCL_WRITE_SECRET",),
        )
        self.assertEqual(1, exit_code)
        self.assertIn("PCL_WRITE_SECRET:", output)
        self.assertIn(RELEASE + ":", output)
        self.assertIn(PREVIEW + ":", output)


if __name__ == "__main__":
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(KeyConfigurationTests))
    print(f"Validated {KeyConfigurationTests.child_process_cases} isolated subprocess cases.")
    raise SystemExit(0 if result.wasSuccessful() else 1)
