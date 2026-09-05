---
name: "embynian-verification"
description: "Verify a code change to the EmbyNian project (C:\\Users\\89400\\EmbyNian): the four gates — Release build, tests, publish, self-check — what passing looks like for each, which self-check report lines are allowed to differ, and screenshot checking per theme. Use after any EmbyNian code change, or when asked to 验证 / 跑一遍闸门."
---

# EmbyNian — running the gates, and what the gates cannot see

Run all four gates after **any** code change. **The commands themselves, the SDK path, the single-node build flags, the switch list and this machine's traps are all in `CLAUDE.md`** — which is loaded whenever this skill is, so read them there. `CLAUDE.md` is the source of truth and wins on any conflict with this file; this file holds the procedure and the traps around it, and deliberately keeps no second copy of anything already written there.

Architecture rules are the `embynian-winui-shell` skill; playback is `embynian-playback`; shaders and 画质档位 are `mpv-shader-quality`.

## The four gates, in order

| # | Gate | Passing looks like |
|---|---|---|
| 1 | build | 0 errors **and** 0 warnings |
| 2 | test | 「全部通过」 with exit code 0 — **only trustworthy when the command carries `-c Release`** |
| 3 | publish | the script completes; it refuses to run at all if `libmpv-2.dll` is missing from the repo root |
| 4 | self-check | exit code 0, 「结果：全部通过」 on the last line of `selfcheck-shell.txt`, and the access token's occurrence count in that report is **0** |

Four things worth knowing before the first command:

- **Gate 2 without `-c Release` is gate 2 switched off.** `dotnet run` looks for Debug output, so with `--no-build` it runs whatever stale binary sits in `bin\Debug` and still prints 「全部通过」 with exit code 0. When this was caught on 2026-08-31 that binary was two days old and 160 tests short of the source.
- Report the passing count as a number observed on this run. **Never write it into a document or a memory** — it grows as development goes on.
- **Gate 3 takes `-NoArchive`** while iterating: it skips the zip nobody looks at.
- **A run that names `--screen` neither reads nor writes the saved window placement.** That is what keeps the report's client-size and 16:9 readings on a stable baseline instead of following whatever size the user last dragged the window to.

## Comparing the report line by line

Ten lines differ every run **by design** — cross those off first, and anything else that moved is worth investigating. **The list lives in [`docs/开发与验证.md`](../../docs/开发与验证.md) under 「逐行比报告：每次都会变的行」, and that is its only live copy**; when a new check adds a volatile line, update it there.

**When 「鼠标真等两秒就藏」 goes red, read `不符：` — not the diagnostic numbers.** That leg carries a dozen counters and every one of them is printed to be read by a human, but only the `不符：` list at the end of the line names the assertion that actually failed. Three reds (09-02, 09-04, and one that went red twice before passing on the third run) were filed against 「someone touched the mouse — the report says so, 轮询问出 1 次移动」, and that number says the opposite: **1 is what a completely undisturbed leg reports** (the probe clears its own 「where is the pointer」 state before the window, so the first poll always counts one; a pointer that truly never moves is filtered out before the counter). 2026-09-05 the probe was corrected to judge disturbance on that count as well as on the end position, and to spell out in the line which of the two it was — so a genuinely disturbed leg now prints 「这一轮只作参考」 and asserts nothing instead of going red. **A red on this leg from here on is a real reading**: get the `不符：` list and the 「空事件」 count into the handover doc before re-running, since a spurious un-hide arrives as a XAML event and is what 08-31 already caught once.

## Screenshots — for what the gates cannot see

Every defect actually caught in this project came from someone looking at the screen: a strip cropped off a poster, an empty patch in the top-left corner, a black border around the episode list. The gates went green on all three.

- **`tools/shot.ps1`** launches, shoots and closes: `-Exe <path> -ExeArgs "--theme daylight --show-settings" -SettleMs N`. When shooting the settings window, pass `--screen 2` and `-WindowTitle 设置` — without the screen argument it raises the window onto the primary monitor, over whatever the user is doing, and this has already photographed the user's game once.
- **`--dump-ui`** writes one screenshot (`selfcheck-shell.png`, the frame after the last page) plus a visual tree. Per-page photography is `shot.ps1`'s job, not the self-check's.
- **Touched colours, spacing or type size → shoot the default theme and `daylight` at least.** `daylight` is the only light one of the six, and hard-coded shell colours plus the system-drawn title bar only show themselves there.
- **「有没有箭头」 needs `tools/cursor-watch.ps1`, never `shot.ps1`.** A GDI screenshot never contains the cursor. `cursor-watch.ps1` prints `GetCursorInfo` as a timeline and, when the flag says a cursor is showing, draws that cursor into the capture with `DrawIconEx` — so 「有箭头」 and 「没有箭头」 become visible in an image. It never touches z-order, the foreground or the cursor position, which is exactly why `shot.ps1` is the wrong tool here: it raises the window topmost and back, and that changes which queue owns the cursor.
- Navigate with the app's own switches rather than the mouse, and never start real playback. Both lists, and why the mouse is not an option here, are in `CLAUDE.md`.

## Other scripts

`smoke.ps1` at the repo root starts the app, waits, then reports whether the process is still alive and prints this run's log. In `tools/`: `verify-publish.ps1`, `backup.ps1`, `shortcut.ps1`, `zoom.ps1`, `poke.ps1`, `cursor-watch.ps1`, `line-icons.awk`. Operating detail that isn't a rule — building for ordinary running, `publish.ps1`'s other switches, what the self-check walks through, where the data directory is — is in [`docs/开发与验证.md`](../../docs/开发与验证.md).
