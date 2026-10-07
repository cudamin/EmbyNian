"""随包 libmpv 的音频选项无声采集器。

用法（仓库根目录）：
    py .claude/skills/embynian-audio-output/scripts/probe-audio-native.py
    py …probe-audio-native.py --dll D:/somewhere/libmpv-2.dll --out work/audio-probe.json

读取默认值、候选选项、滤镜循环与共享/失效端点/null AO 的零信号用例。
固定 config=no、load-scripts=no、vo=null、video=no、audio-exclusive=no。
安全选项失败即中止并释放句柄；不读用户配置、不播放媒体库、不申请独占。
退出0只表示采集完整，须判读报告中的返回码、读数、警告和预期失败。
"""
import argparse
from contextlib import ExitStack
import ctypes as C
import hashlib
import json
import os
import time
from pathlib import Path


BASE = {"config": "no", "load-scripts": "no", "terminal": "no", "video": "no", "vo": "null",
        "osc": "no", "ytdl": "no", "idle": "yes", "audio-display": "no", "input-default-bindings": "no",
        "audio-exclusive": "no"}
DYNA = "@dynaudnorm:lavfi=[dynaudnorm=f=500:g=31:p=0.5:m=5:r=0.9]"
LOUD = "@loudnorm:lavfi=[loudnorm=I=-16:TP=-1.5:LRA=11]"
MPV = None


def repo_root() -> Path:
    return Path(__file__).resolve().parents[4]


class Event(C.Structure):
    _fields_ = [("event_id", C.c_int), ("error", C.c_int), ("reply_userdata", C.c_uint64), ("data", C.c_void_p)]


class Log(C.Structure):
    _fields_ = [("prefix", C.c_char_p), ("level", C.c_char_p), ("text", C.c_char_p), ("log_level", C.c_int)]


def load_dll(path: Path):
    directories = [path.parent, path.parent / "mpv-runtime", path.parent / "assets/mpv-runtime"]
    stack = ExitStack()
    try:
        if os.name == "nt":
            for directory in directories:
                if directory.is_dir():
                    stack.enter_context(os.add_dll_directory(str(directory)))
        mpv = C.CDLL(str(path))
        mpv.mpv_create.restype = C.c_void_p
        mpv.mpv_initialize.argtypes = [C.c_void_p]
        mpv.mpv_initialize.restype = C.c_int
        mpv.mpv_set_option_string.argtypes = [C.c_void_p, C.c_char_p, C.c_char_p]
        mpv.mpv_set_option_string.restype = C.c_int
        mpv.mpv_set_property_string.argtypes = [C.c_void_p, C.c_char_p, C.c_char_p]
        mpv.mpv_set_property_string.restype = C.c_int
        mpv.mpv_get_property_string.argtypes = [C.c_void_p, C.c_char_p]
        mpv.mpv_get_property_string.restype = C.c_void_p
        mpv.mpv_command.argtypes = [C.c_void_p, C.POINTER(C.c_char_p)]
        mpv.mpv_command.restype = C.c_int
        mpv.mpv_free.argtypes = [C.c_void_p]
        mpv.mpv_terminate_destroy.argtypes = [C.c_void_p]
        mpv.mpv_request_log_messages.argtypes = [C.c_void_p, C.c_char_p]
        mpv.mpv_request_log_messages.restype = C.c_int
        mpv.mpv_wait_event.argtypes = [C.c_void_p, C.c_double]
        mpv.mpv_wait_event.restype = C.POINTER(Event)
        mpv._directory_stack = stack
        return mpv
    except BaseException:
        stack.close()
        raise


def decode(pointer):
    return pointer.decode("utf-8", "replace") if pointer else None


class Session:
    def __init__(self, options=None):
        selected = dict(options or {})
        for name, value in selected.items():
            if name in BASE and value != BASE[name]:
                raise ValueError(f"不能覆盖隔离选项 {name}")
        self.handle = MPV.mpv_create()
        if not self.handle:
            raise RuntimeError("mpv_create 返回空句柄")
        self.logs = []
        self.option_results = {}
        try:
            for name, value in {**BASE, **selected}.items():
                code = MPV.mpv_set_option_string(self.handle, name.encode(), str(value).encode())
                self.option_results[name] = code
                if code < 0:
                    raise RuntimeError(f"设置选项 {name} 失败：{code}")
            code = MPV.mpv_initialize(self.handle)
            if code < 0:
                raise RuntimeError(f"initialize 失败：{code}")
            code = MPV.mpv_request_log_messages(self.handle, b"v")
            if code < 0:
                raise RuntimeError(f"订阅日志失败：{code}")
        except BaseException:
            self.close()
            raise

    def prop(self, name):
        pointer = MPV.mpv_get_property_string(self.handle, name.encode())
        if not pointer:
            return None
        try:
            return C.string_at(pointer).decode("utf-8", "replace")
        finally:
            MPV.mpv_free(pointer)

    def set(self, name, value):
        return MPV.mpv_set_property_string(self.handle, name.encode(), str(value).encode())

    def command(self, *args):
        data = (C.c_char_p * (len(args) + 1))(*[str(a).encode() for a in args], None)
        return MPV.mpv_command(self.handle, data)

    def pump(self, seconds):
        until = time.monotonic() + seconds
        while time.monotonic() < until:
            event = MPV.mpv_wait_event(self.handle, min(0.025, max(0, until - time.monotonic()))).contents
            if event.event_id == 2 and event.data:
                message = C.cast(event.data, C.POINTER(Log)).contents
                self.logs.append({"prefix": decode(message.prefix), "level": decode(message.level),
                                  "text": decode(message.text).strip()})

    def snapshot(self):
        return {key: self.prop(key) for key in
                ["audio-device", "current-ao", "audio-out-params", "audio-params", "audio-spdif",
                 "audio-channels", "audio-exclusive", "audio-delay", "volume", "af", "idle-active"]}

    def close(self):
        if self.handle:
            MPV.mpv_terminate_destroy(self.handle)
            self.handle = None


def collect(report):
    session = Session({"ao": "null"})
    try:
        report["versions"] = {key: session.prop(key) for key in ["mpv-version", "ffmpeg-version"]}
        report["bootstrap_options"] = session.option_results
        names = ["audio-device", "ao", "audio-channels", "audio-spdif", "audio-exclusive", "ad-lavc-ac3drc",
                 "audio-normalize-downmix", "audio-pitch-correction", "audio-samplerate", "audio-format",
                 "gapless-audio", "replaygain", "volume", "volume-max", "audio-delay", "af"]
        report["option_info"] = {name: {kind: session.prop(f"option-info/{name}/{kind}") for kind in
                                        ["default-value", "choices", "min", "max"]} for name in names}
        values = {"audio-channels": ["auto-safe", "auto", "stereo", "5.1", "7.1", "7.1,5.1,stereo"],
                  "ad-lavc-ac3drc": ["0", "0.5", "1"], "audio-spdif": ["ac3,eac3,dts,dts-hd,truehd", ""],
                  "af": [DYNA, LOUD, ""], "audio-delay": ["-5", "0.001", "5", "0"], "volume": ["0", "100", "130"]}
        report["accepted_values"] = {name: [{"value": value, "code": session.set(name, value), "readback": session.prop(name)}
                                            for value in candidates]
                                     for name, candidates in values.items()}
        report["filter_cycles"] = []
        for _ in range(4):
            code = session.command("cycle-values", "af", "", DYNA, LOUD)
            report["filter_cycles"].append({"code": code, "af": session.prop("af")})
        report["delay_nudges"] = []
        session.set("audio-delay", "0.9")
        for _ in range(3):
            code = session.command("add", "audio-delay", "0.1")
            report["delay_nudges"].append({"code": code, "seconds": session.prop("audio-delay")})
        report["delay_reset"] = {"code": session.command("set", "audio-delay", "0"), "seconds": session.prop("audio-delay")}
    finally:
        session.close()

    report["silent_device_cases"] = []
    for label, options in [
        ("shared-auto", {"ao": "wasapi", "audio-device": "auto"}),
        ("missing-endpoint", {"audio-device": "wasapi/{00000000-0000-0000-0000-000000000000}"}),
        ("null-dyna", {"ao": "null", "audio-channels": "stereo", "af": DYNA}),
        ("null-loud", {"ao": "null", "audio-channels": "stereo", "af": LOUD}),
    ]:
        session = Session(options)
        try:
            code = session.command("loadfile", "av://lavfi:anullsrc=r=48000:cl=5.1:d=12", "replace")
            session.pump(2.0)
            report["silent_device_cases"].append({"name": label, "option_results": session.option_results, "load_code": code,
                "during": session.snapshot(),
                "warnings": [entry for entry in session.logs if entry["level"] in ("warn", "error", "fatal")]})
        finally:
            session.close()


def main(argv=None):
    global MPV
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dll", type=Path, default=repo_root() / "libmpv-2.dll")
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args(argv)
    dll = args.dll.resolve()
    output = args.out or repo_root() / "work" / f"audio-probe-{time.strftime('%Y%m%d-%H%M%S')}.json"
    if output.exists():
        parser.error("报告已存在，请使用新的 --out 路径")
    output.parent.mkdir(parents=True, exist_ok=True)
    report = {"dll": str(dll), "scope": "无声隔离采集：不发可闻信号、不申请独占、不读用户配置", "completed": False}
    code = 1
    try:
        report["dll_sha256"] = hashlib.sha256(dll.read_bytes()).hexdigest()
        MPV = load_dll(dll)
        collect(report)
        report["completed"] = True
        code = 0
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    finally:
        if MPV is not None:
            MPV._directory_stack.close()
            MPV = None
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(output), "completed": report["completed"], "error": report.get("error"),
                      "mpv": report.get("versions", {}).get("mpv-version"),
                      "cases": [{"name": case["name"], "current_ao": case["during"]["current-ao"],
                                 "warnings": [entry["text"] for entry in case["warnings"]]}
                                for case in report.get("silent_device_cases", [])]}, ensure_ascii=False, indent=2))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
