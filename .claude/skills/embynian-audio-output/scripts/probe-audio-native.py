"""随包 libmpv 的音频选项无声探针：问内核要默认值与接受性，不用手册或注释当事实。

用法（仓库根目录运行）：
    py .claude/skills/embynian-audio-output/scripts/probe-audio-native.py
    py …probe-audio-native.py --dll D:/somewhere/libmpv-2.dll --out work/audio-probe.json

做的事（2026-09-30 审查的原始版本，证据与判读见 SKILL.md 与 work/audio-output-audit-20260930.md）：
  ① option-info 读默认值/choices——ac3drc 0、channels auto-safe、volume-max 130 这类「出厂事实」以这里为准；
  ② 候选值逐个 set 再读回，收接受性与读数（audio-channels 列表、两条响度滤镜、延迟、音量边界）；
  ③ cycle-values af 循环与空串复位（播放器菜单那条路的内核语义）；
  ④ 四个无声用例：共享 auto（成功对照）、失效端点（内核不回退的证据）、null AO 挂 dynaudnorm/loudnorm。

约束：config=no、vo=null、video=no，夹具是 anullsrc 零信号——不发可闻信号、不申请独占、
不读用户 mpv 配置。写 JSON 报告 + 控制台摘要；退出码 0 只代表跑完，不代表每条读数「正确」。
"""
import argparse
import ctypes as C
import json
import time
from pathlib import Path


def repo_root() -> Path:
    return Path(__file__).resolve().parents[4]


def load_dll(path: Path):
    os_add = __import__("os").add_dll_directory
    os_add(str(path.parent))
    runtime = path.parent / "mpv-runtime"
    if runtime.is_dir():
        os_add(str(runtime))
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
    mpv.mpv_wait_event.argtypes = [C.c_void_p, C.c_double]
    mpv.mpv_wait_event.restype = C.POINTER(Event)
    return mpv


class Event(C.Structure):
    _fields_ = [("event_id", C.c_int), ("error", C.c_int), ("reply_userdata", C.c_uint64), ("data", C.c_void_p)]


class Log(C.Structure):
    _fields_ = [("prefix", C.c_char_p), ("level", C.c_char_p), ("text", C.c_char_p), ("log_level", C.c_int)]


BASE = {"config": "no", "load-scripts": "no", "terminal": "no", "video": "no", "vo": "null",
        "osc": "no", "ytdl": "no", "idle": "yes", "audio-display": "no", "input-default-bindings": "no"}
DYNA = "@dynaudnorm:lavfi=[dynaudnorm=f=500:g=31:p=0.5:m=5:r=0.9]"
LOUD = "@loudnorm:lavfi=[loudnorm=I=-16:TP=-1.5:LRA=11]"


def decode(pointer):
    return pointer.decode("utf-8", "replace") if pointer else None


class Session:
    def __init__(self, options=None):
        self.handle = load_dll(DLL).mpv_create()
        if not self.handle:
            raise RuntimeError("mpv_create 返回空句柄")
        self.logs = []
        self.option_results = {}
        for name, value in {**BASE, **(options or {})}.items():
            self.option_results[name] = MPV.mpv_set_option_string(self.handle, name.encode(), str(value).encode())
        code = MPV.mpv_initialize(self.handle)
        if code < 0:
            self.close()
            raise RuntimeError(f"initialize 失败：{code}")
        MPV.mpv_request_log_messages(self.handle, b"v")

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


parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument("--dll", type=Path, default=repo_root() / "libmpv-2.dll")
parser.add_argument("--out", type=Path, default=None)
args = parser.parse_args()
DLL = args.dll.resolve()
OUT = args.out or repo_root() / "work" / f"audio-probe-{time.strftime('%Y%m%d-%H%M%S')}.json"
MPV = load_dll(DLL)
OUT.parent.mkdir(parents=True, exist_ok=True)

report = {"dll": str(DLL), "scope": "无声隔离探针：不发可闻信号、不申请独占、不读用户配置"}
session = Session({"ao": "null"})
try:
    report["versions"] = {key: session.prop(key) for key in ["mpv-version", "ffmpeg-version"]}
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
finally:
    session.close()

report["silent_device_cases"] = []
for label, options in [
    ("shared-auto", {"ao": "wasapi", "audio-device": "auto", "audio-exclusive": "no"}),
    ("missing-endpoint", {"audio-device": "wasapi/{00000000-0000-0000-0000-000000000000}", "audio-exclusive": "no"}),
    ("null-dyna", {"ao": "null", "audio-channels": "stereo", "af": DYNA}),
    ("null-loud", {"ao": "null", "audio-channels": "stereo", "af": LOUD}),
]:
    session = Session(options)
    try:
        code = session.command("loadfile", "av://lavfi:anullsrc=r=48000:cl=5.1:d=12", "replace")
        session.pump(2.0)
        case = {"name": label, "option_results": session.option_results, "load_code": code,
                "during": session.snapshot(),
                "warnings": [entry for entry in session.logs if entry["level"] in ("warn", "error", "fatal")]}
        report["silent_device_cases"].append(case)
    finally:
        session.close()

OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"report": str(OUT), "mpv": report["versions"]["mpv-version"],
                  "defaults": {name: item["default-value"] for name, item in report["option_info"].items()},
                  "cases": [{"name": case["name"], "current_ao": case["during"]["current-ao"],
                             "warnings": [entry["text"] for entry in case["warnings"]]}
                            for case in report["silent_device_cases"]]}, ensure_ascii=False, indent=2))
