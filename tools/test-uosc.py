"""Run the real bundled uosc with local, headless libmpv and deterministic input fixtures."""
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import sys
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = args.root.resolve()
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
source = root / 'assets/mpv-ui/scripts/uosc'
tests = root / 'tests/Momoka.Tests/Lua/uosc-interaction-tests.lua'
script = output / 'uosc'
shutil.copytree(source, script)
main = script / 'main.lua'
prelude = '''local fixture_mp = require('mp')
fixture_events = {}
fixture_messages = {}
local fixture_message = fixture_mp.register_script_message
fixture_mp.register_script_message = function(name, callback)
    fixture_messages[name] = callback
    return fixture_message(name, callback)
end
local fixture_register = fixture_mp.register_event
fixture_mp.register_event = function(name, callback)
    fixture_events[name] = fixture_events[name] or {}
    table.insert(fixture_events[name], callback)
    return fixture_register(name, callback)
end
'''
main.write_text(prelude + main.read_text(encoding='utf-8') + '\n' + tests.read_text(encoding='utf-8'), encoding='utf-8', newline='\n')
manifest = {str(file.relative_to(source)).replace('\\', '/'): hashlib.sha256(file.read_bytes()).hexdigest()
            for file in source.rglob('*') if file.is_file()}
manifest['fixture'] = hashlib.sha256(tests.read_bytes()).hexdigest()
(output / 'source-hashes.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
search_paths = [os.add_dll_directory(str(root)), os.add_dll_directory(str(root / 'assets/mpv-runtime'))]
mpv = ctypes.CDLL(str(root / 'libmpv-2.dll'))

class Event(ctypes.Structure):
    _fields_ = [('id', ctypes.c_int), ('error', ctypes.c_int), ('userdata', ctypes.c_uint64), ('data', ctypes.c_void_p)]

class Message(ctypes.Structure):
    _fields_ = [('count', ctypes.c_int), ('args', ctypes.POINTER(ctypes.c_char_p))]

class Log(ctypes.Structure):
    _fields_ = [('prefix', ctypes.c_char_p), ('level', ctypes.c_char_p), ('text', ctypes.c_char_p), ('log_level', ctypes.c_int)]

mpv.mpv_create.restype = ctypes.c_void_p
mpv.mpv_initialize.argtypes = [ctypes.c_void_p]
mpv.mpv_set_option_string.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p]
mpv.mpv_command.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_char_p)]
mpv.mpv_request_log_messages.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
mpv.mpv_wait_event.argtypes = [ctypes.c_void_p, ctypes.c_double]
mpv.mpv_wait_event.restype = ctypes.POINTER(Event)
mpv.mpv_terminate_destroy.argtypes = [ctypes.c_void_p]
handle = mpv.mpv_create()
checks = []
errors = []
finished = False
try:
    for name, value in {
        'config': 'no', 'load-scripts': 'no', 'osc': 'no', 'load-stats-overlay': 'no',
        'terminal': 'no', 'vo': 'null', 'ao': 'null', 'video': 'no', 'audio': 'no', 'idle': 'yes',
        'access-references': 'no', 'load-unsafe-playlists': 'no', 'input-default-bindings': 'no',
        'osd-fonts-dir': str(root / 'assets/mpv-ui/fonts'), 'osd-font': 'Microsoft YaHei',
        'scripts': str(script),
    }.items():
        if mpv.mpv_set_option_string(handle, name.encode(), value.encode()) < 0:
            raise RuntimeError('Rejected option: ' + name)
    mpv.mpv_request_log_messages(handle, b'error')
    if mpv.mpv_initialize(handle) < 0:
        raise RuntimeError('mpv initialization failed')
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline and not finished:
        event = mpv.mpv_wait_event(handle, 0.05).contents
        if event.id == 16:
            message = ctypes.cast(event.data, ctypes.POINTER(Message)).contents
            values = [message.args[i].decode('utf-8', 'replace') for i in range(message.count)]
            if values and values[0] == 'fixture-result':
                checks = json.loads(values[1])
                finished = True
        elif event.id == 2:
            log = ctypes.cast(event.data, ctypes.POINTER(Log)).contents
            errors.append(log.text.decode('utf-8', 'replace').strip())
finally:
    mpv.mpv_terminate_destroy(handle)
    for directory in search_paths:
        directory.close()
report = {'completed': finished, 'checks': checks, 'errors': errors,
          'coverage': 'Bundled libmpv Lua runtime; fake input, properties and clock; no media, window, HTTP or user settings.'}
(output / 'results.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
for check in checks:
    print(('PASS ' if check['passed'] else 'FAIL ') + check['name'] + ': ' + check.get('detail', ''))
print(f'Completed={finished}; checks={len(checks)}; failed={sum(not c["passed"] for c in checks)}; errors={len(errors)}')
sys.exit(0 if finished and checks and all(c['passed'] for c in checks) and not errors else 1)
