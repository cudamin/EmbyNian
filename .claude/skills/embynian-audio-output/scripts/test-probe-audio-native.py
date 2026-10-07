"""Offline regressions for the native audio collector; never loads libmpv or opens an audio endpoint."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import sys

sys.dont_write_bytecode = True


spec = importlib.util.spec_from_file_location("audio_probe", Path(__file__).with_name("probe-audio-native.py"))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class FakeMpv:
    def __init__(self, fail_option=None, fail_init=False, fail_log=False, empty=False):
        self.fail_option = fail_option
        self.fail_init = fail_init
        self.fail_log = fail_log
        self.empty = empty
        self.initialized = 0
        self.closed = 0
        self.created = 0
        self.options = {}

    def mpv_create(self):
        self.created += 1
        return 0 if self.empty else 1

    def mpv_set_option_string(self, handle, name, value):
        self.options[name.decode()] = value.decode()
        return -1 if name.decode() == self.fail_option else 0

    def mpv_initialize(self, handle):
        self.initialized += 1
        return -1 if self.fail_init else 0

    def mpv_request_log_messages(self, handle, level):
        return -1 if self.fail_log else 0

    def mpv_terminate_destroy(self, handle):
        self.closed += 1


class CollectorTests(unittest.TestCase):
    def tearDown(self):
        probe.MPV = None

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
            with self.assertRaises(SystemExit):
                probe.main(["--out", str(report)])
            self.assertEqual(report.read_text(encoding="utf-8"), "original")


if __name__ == "__main__":
    unittest.main(verbosity=2)
