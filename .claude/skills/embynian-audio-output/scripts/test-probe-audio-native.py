"""Offline regressions for the native audio collector; never loads libmpv or opens an audio endpoint."""
import importlib.util
from contextlib import ExitStack, redirect_stderr, redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import sys

sys.dont_write_bytecode = True


spec = importlib.util.spec_from_file_location("audio_probe", Path(__file__).with_name("probe-audio-native.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class FakeMpv:
    def __init__(self, fail_option=None, fail_init=False, fail_log=False, empty=False, fail_session=None):
        self.fail_option = fail_option
        self.fail_init = fail_init
        self.fail_log = fail_log
        self.empty = empty
        self.fail_session = fail_session
        self.initialized = 0
        self.closed = 0
        self.created = 0
        self.options = {}
        self._directory_stack = ExitStack()

    def failing(self):
        return self.fail_session is None or self.created == self.fail_session

    def mpv_create(self):
        self.created += 1
        return 0 if self.empty and self.failing() else self.created

    def mpv_set_option_string(self, handle, name, value):
        self.options[name.decode()] = value.decode()
        return -1 if name.decode() == self.fail_option and self.failing() else 0

    def mpv_initialize(self, handle):
        self.initialized += 1
        return -1 if self.fail_init and self.failing() else 0

    def mpv_request_log_messages(self, handle, level):
        return -1 if self.fail_log and self.failing() else 0

    def mpv_terminate_destroy(self, handle):
        self.closed += 1


class CollectorTests(unittest.TestCase):
    def tearDown(self):
        probe.MPV = None

    def run_collector(self, fake, pump_error=None):
        """Exercise the real main/collect/Session flow, replacing only native calls and media reads."""
        with tempfile.TemporaryDirectory(prefix="audio-collector-") as directory:
            root = Path(directory)
            dll = root / "fake.dll"
            dll.write_bytes(b"offline fixture, never loaded")
            output = root / "report.json"
            stdout = io.StringIO()
            with patch.object(probe, "load_dll", return_value=fake), \
                    patch.object(probe.Session, "prop", return_value="fixture"), \
                    patch.object(probe.Session, "set", return_value=0), \
                    patch.object(probe.Session, "command", return_value=0), \
                    patch.object(probe.Session, "pump", side_effect=pump_error), redirect_stdout(stdout):
                code = probe.main(["--dll", str(dll), "--out", str(output)])
            return code, json.loads(output.read_text(encoding="utf-8")), json.loads(stdout.getvalue())

    def test_rejects_safety_overrides_before_create(self):
        fake = probe.MPV = FakeMpv()
        for name in ("config", "load-scripts", "video", "vo", "audio-exclusive"):
            with self.assertRaises(ValueError):
                probe.Session({name: "unsafe"})
        self.assertEqual(fake.created, 0)

    def test_failed_safety_option_never_initializes(self):
        for name in probe.BASE:
            fake = probe.MPV = FakeMpv(fail_option=name)
            with self.assertRaises(RuntimeError):
                probe.Session({"ao": "null"})
            self.assertEqual(fake.initialized, 0)
            self.assertEqual(fake.closed, 1)

    def test_failed_case_option_also_aborts(self):
        fake = probe.MPV = FakeMpv(fail_option="ao")
        with self.assertRaises(RuntimeError):
            probe.Session({"ao": "null"})
        self.assertEqual(fake.initialized, 0)
        self.assertEqual(fake.closed, 1)

    def test_initialization_failure_closes(self):
        fake = probe.MPV = FakeMpv(fail_init=True)
        with self.assertRaises(RuntimeError):
            probe.Session()
        self.assertEqual(fake.closed, 1)

    def test_log_failure_closes(self):
        fake = probe.MPV = FakeMpv(fail_log=True)
        with self.assertRaises(RuntimeError):
            probe.Session()
        self.assertEqual(fake.closed, 1)

    def test_success_closes_once_and_sets_shared_audio(self):
        fake = probe.MPV = FakeMpv()
        session = probe.Session({"ao": "null"})
        self.assertEqual(fake.options["audio-exclusive"], "no")
        self.assertEqual(fake.options["config"], "no")
        self.assertEqual(fake.initialized, 1)
        self.assertEqual(session.record["initialize_code"], 0)
        self.assertEqual(session.record["log_request_code"], 0)
        session.close()
        session.close()
        self.assertEqual(fake.closed, 1)

    def test_null_handle_does_not_initialize_or_destroy(self):
        fake = probe.MPV = FakeMpv(empty=True)
        with self.assertRaises(RuntimeError):
            probe.Session()
        self.assertEqual(fake.initialized, 0)
        self.assertEqual(fake.closed, 0)

    def test_missing_library_records_incomplete_report(self):
        with tempfile.TemporaryDirectory(prefix="audio-collector-") as directory:
            root = Path(directory)
            report = root / "result.json"
            self.assertEqual(probe.main(["--dll", str(root / "missing.dll"), "--out", str(report)]), 1)
            result = json.loads(report.read_text(encoding="utf-8"))
            self.assertFalse(result["completed"])
            self.assertIn("FileNotFoundError", result["error"])

    def test_existing_report_is_not_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="audio-collector-") as directory:
            report = Path(directory) / "result.json"
            report.write_text("original", encoding="utf-8")
            with patch.object(probe, "load_dll") as loader, redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    probe.main(["--out", str(report)])
                self.assertEqual(raised.exception.code, 2)
                loader.assert_not_called()
            self.assertEqual(report.read_text(encoding="utf-8"), "original")

    def test_report_created_by_competing_run_is_not_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="audio-collector-") as directory:
            report = Path(directory) / "report.json"
            original_open = Path.open

            def competing_open(path, mode="r", *args, **kwargs):
                if path == report and mode == "x":
                    # Another process wins immediately before this process tries to reserve the path.
                    with original_open(path, "x", encoding="utf-8") as other:
                        other.write("other run's evidence")
                return original_open(path, mode, *args, **kwargs)

            with patch.object(Path, "open", competing_open), patch.object(probe, "load_dll") as loader, \
                    redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    probe.main(["--out", str(report)])
                self.assertEqual(raised.exception.code, 2)
                loader.assert_not_called()
            self.assertEqual(report.read_text(encoding="utf-8"), "other run's evidence")

    def test_complete_report_keeps_successful_startup_codes(self):
        fake = FakeMpv()
        code, report, summary = self.run_collector(fake)
        self.assertEqual(code, 0)
        self.assertTrue(report["completed"])
        self.assertTrue(summary["completed"])
        self.assertEqual(len(report["silent_device_cases"]), 4)
        for record in [report["bootstrap"], *report["silent_device_cases"]]:
            self.assertEqual(record["initialize_code"], 0)
            self.assertEqual(record["log_request_code"], 0)
            self.assertEqual(record["stage"], "complete")
            self.assertTrue(record["completed"])
        self.assertEqual(fake.closed, 5)

    def test_bootstrap_failures_keep_partial_startup_evidence(self):
        scenarios = [
            ({"empty": True}, "create", None, None),
            ({"fail_option": "ao"}, "option:ao", None, None),
            ({"fail_init": True}, "initialize", -1, None),
            ({"fail_log": True}, "request-log-messages", 0, -1),
        ]
        for failures, stage, init_code, log_code in scenarios:
            with self.subTest(stage=stage):
                fake = FakeMpv(**failures)
                code, report, summary = self.run_collector(fake)
                self.assertEqual(code, 1)
                self.assertFalse(report["completed"])
                record = report["bootstrap"]
                self.assertFalse(record["completed"])
                self.assertEqual(record["name"], "bootstrap")
                self.assertEqual(record["stage"], stage)
                self.assertEqual(record["options"]["ao"], "null")
                self.assertEqual(record["error"], report["error"])
                self.assertEqual(summary["error"], report["error"])
                self.assertEqual(report["bootstrap_options"], record["option_results"])
                for key, expected in [("initialize_code", init_code), ("log_request_code", log_code)]:
                    if expected is None:
                        self.assertNotIn(key, record)
                    else:
                        self.assertEqual(record[key], expected)
                if stage != "create":
                    self.assertEqual(record["option_results"]["config"], 0)
                    self.assertEqual(record["option_results"]["ao"], -1 if stage == "option:ao" else 0)
                self.assertEqual(fake.closed, 0 if stage == "create" else 1)

    def test_late_case_failure_keeps_case_and_prints_partial_summary(self):
        fake = FakeMpv(fail_option="af", fail_session=5)
        code, report, summary = self.run_collector(fake)
        self.assertEqual(code, 1)
        self.assertFalse(report["completed"])
        cases = report["silent_device_cases"]
        self.assertEqual([case["name"] for case in cases],
                         ["shared-auto", "missing-endpoint", "null-dyna", "null-loud"])
        self.assertTrue(all(case["completed"] for case in cases[:3]))
        failed = cases[-1]
        self.assertFalse(failed["completed"])
        self.assertEqual(failed["stage"], "option:af")
        self.assertEqual(failed["options"]["af"], probe.LOUD)
        self.assertEqual(failed["option_results"]["config"], 0)
        self.assertEqual(failed["option_results"]["af"], -1)
        self.assertNotIn("initialize_code", failed)
        self.assertNotIn("during", failed)
        self.assertNotIn("warnings", failed)
        self.assertEqual(summary["cases"][-1]["error"], failed["error"])
        self.assertIsNone(summary["cases"][-1]["current_ao"])
        self.assertEqual(summary["cases"][-1]["warnings"], [])
        self.assertEqual(fake.closed, 5)

    def test_failure_after_load_keeps_command_and_startup_evidence(self):
        fake = FakeMpv()
        code, report, summary = self.run_collector(fake, pump_error=RuntimeError("pump failed"))
        self.assertEqual(code, 1)
        failed = report["silent_device_cases"][0]
        self.assertEqual(failed["name"], "shared-auto")
        self.assertFalse(failed["completed"])
        self.assertEqual(failed["stage"], "pump")
        self.assertEqual(failed["load_code"], 0)
        self.assertEqual(failed["initialize_code"], 0)
        self.assertEqual(failed["log_request_code"], 0)
        self.assertEqual(summary["cases"][0]["error"], "RuntimeError: pump failed")
        self.assertEqual(fake.closed, 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
