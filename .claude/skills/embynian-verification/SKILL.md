---
name: "embynian-verification"
description: "Verify a code change to the EmbyNian project (C:\\Users\\89400\\EmbyNian): the gates — Release build, tests, publish after any change, plus the self-check when a version ships or when the self-check itself changed — what passing looks like for each, which self-check report lines are allowed to differ, and screenshot checking per theme. Use after any EmbyNian code change, or when asked to 验证 / 跑一遍闸门 / 发新版本."
---

# EmbyNian — running the gates, and what the gates cannot see

**Build, test and publish after any code change. The self-check runs when a version ships** (his call, 2026-09-12: 「现在只在发布新版本的时候跑自检」) **and whenever the self-check's own code changed** — a change to the checks that nobody ran is an unverified change. **The commands themselves, the SDK path, the single-node build flags, the switch list, that rule and this machine's traps are all in `CLAUDE.md`** — which is loaded whenever this skill is, so read them there. `CLAUDE.md` is the source of truth and wins on any conflict with this file; this file holds the procedure and the traps around it, and deliberately keeps no second copy of anything already written there.

Architecture rules are the `embynian-winui-shell` skill; playback is `embynian-playback`; shaders and 画质档位 are `mpv-shader-quality`.

## The gates, in order

| # | Gate | When | Passing looks like |
|---|---|---|---|
| 1 | build | any change | 0 errors **and** 0 warnings |
| 2 | test | any change | 「全部通过」 with exit code 0 — **only trustworthy when the command carries `-c Release`** |
| 3 | publish | any change | the script completes; it refuses to run at all if `libmpv-2.dll` is missing from the repo root |
| 4 | self-check | a version ships, or the self-check changed | exit code 0, 「结果：全部通过」 on the last line of `selfcheck-shell.txt`, and the access token's occurrence count in that report is **0** |

Four things worth knowing before the first command:

- **Gate 2 without `-c Release` is gate 2 switched off.** `dotnet run` looks for Debug output, so with `--no-build` it runs whatever stale binary sits in `bin\Debug` and still prints 「全部通过」 with exit code 0. When this was caught on 2026-08-31 that binary was two days old and 160 tests short of the source.
- Report the passing count as a number observed on this run. **Never write it into a document or a memory** — it grows as development goes on.
- **Gate 3 takes `-NoArchive`** while iterating: it skips the zip nobody looks at.
- **A run that names `--screen` neither reads nor writes the saved window placement.** That is what keeps the report's client-size and browse-shape readings on a stable baseline instead of following whatever size the user last dragged the window to.

## Comparing the report line by line

Ten lines differ every run **by design** — cross those off first, and anything else that moved is worth investigating. **The list lives in [`docs/开发与验证.md`](../../docs/开发与验证.md) under 「逐行比报告：每次都会变的行」, and that is its only live copy**; when a new check adds a volatile line, update it there.

**When 「鼠标真等两秒就藏」 goes red, read `不符：` — not the diagnostic numbers.** That leg carries a dozen counters and every one of them is printed to be read by a human, but only the `不符：` list at the end of the line names the assertion that actually failed. Three reds (09-02, 09-04, and one that went red twice before passing on the third run) were filed against 「someone touched the mouse — the report says so, 轮询问出 1 次移动」, and that number says the opposite: **1 is what a completely undisturbed leg reports** (the probe clears its own 「where is the pointer」 state before the window, so the first poll always counts one; a pointer that truly never moves is filtered out before the counter). 2026-09-05 the probe was corrected to judge disturbance on that count as well as on the end position, and to spell out in the line which of the two it was — so a genuinely disturbed leg now prints 「这一轮只作参考」 and asserts nothing instead of going red. **A red on this leg from here on is a real reading**: get the `不符：` list and the 「空事件」 count into the handover doc before re-running, since a spurious un-hide arrives as a XAML event and is what 08-31 already caught once.

**Two half-legs of the same check carry the cursor's two failure modes**, and both are judged rather than printed: 「藏好后挪一个像素：过后还藏着」 (a one-pixel displacement — desk rattle, sensor drift, or the player's own ask — must not wake it; added 2026-09-12 for 「鼠标隐藏了一会又会自动跑出来」, verified red by setting `ChromeReveal.MovePixels` to 1) and 「挪一下就回来」 (a real displacement must wake it at once). The first one prints 「指针没挪动，这一句只作参考」 when `MovePointerTo`/`SetCursorPos` could not move anything, which is the only tolerance it has — unlike the old legs it does not depend on the island hearing injected input.

## Moving the pointer, and proving it moved

`CLAUDE.md` states the rule (navigate with the app's own switches, never the mouse) and why it exists. This section holds what was measured behind it — re-measured 2026-09-05 with a message-counting window parked under the cursor, because the old blanket 「程序里挪鼠标一律没用」 was what made 「让系统重新问一次」 get written as an injection that never fired.

| Attempt | What it produces | Does the pointer move |
|---|---|---|
| `SendInput` relative move of (0,0) | nothing at all — 0 `WM_MOUSEMOVE`, 0 `WM_SETCURSOR`, return value 1 either way | no |
| `SetCursorPos` to the point it already occupies | one of each, **and from a process that is not in the foreground** | no |
| `SendInput` with a real displacement | moved it in one run, not in another — don't lean on it | sometimes |
| `SetCursorPos` with a real displacement | twice out of twice, from a terminal, onto a window on the other monitor | yes |

Two consequences, and they are the same measurement from two sides:

- **A move only reaches the XAML island if it goes through the input queue, and only if it is a real displacement.** `SetCursorPos` moves the coordinate without producing input, so the island hears nothing — 「XAML 事件 0 次」 for a pointer it demonstrably moved. A zero-displacement `SendInput` produces nothing at all, which the self-check printed for months as 「真实输入注不进」. **A one-pixel `SendInput` out and straight back is heard** (「真实输入到位」, same probe, same machine), and it is the only lever that makes WinUI re-read `ProtectedCursor` — i.e. that reaches the pixels a pointer sitting over XAML content is drawn from. So: to make the framework look again, inject a real displacement and undo it; to prove that it looked, read the island's own pointer-event count, **never `GetCursorInfo`**.
- **Don't build a screenshot on a parked pointer.** `SetCursorPos` really can put it over a hover target, but the user's own hand is on the same mouse and the hover is gone by the time the shutter opens — this was tried on the home rail's paging bars and the photograph came back without them. Hover-only affordances get a `--show-*` switch instead.

## Screenshots — for what the gates cannot see

Every defect actually caught in this project came from someone looking at the screen: a strip cropped off a poster, an empty patch in the top-left corner, a black border around the episode list. The gates went green on all three.

- **`tools/shot.ps1`** launches, shoots and closes: `-Exe <path> -ExeArgs "--theme midnight --show-settings" -SettleMs N`. When shooting the settings window, pass `--screen 2` and `-WindowTitle 设置` — without the screen argument it raises the window onto the primary monitor, over whatever the user is doing, and this has already photographed the user's game once.
- **A shot at a chosen window size is `work/shot-resize.ps1`** — `tools/shot.ps1` plus `-ResizeWidth/-ResizeHeight` (a `SetWindowPos` between raising the window and settling; that is where the 866- and 1554-wide detail-page artifacts came from). Run it from the PowerShell tool, never by calling powershell out of bash — that call is blocked outright — and expect no stdout back: the proof is the PNG itself, not the script's last line.
- **`--dump-ui`** writes one screenshot (`selfcheck-shell.png`, the frame after the last page) plus a visual tree. Per-page photography is `shot.ps1`'s job, not the self-check's.
- **Touched colours, spacing or type size → shoot the default theme, and one more if the change could read differently on another.** All five themes are dark since `daylight` was deleted (2026-09-05, the user's call), so there is no longer a light theme to shoot — which is also why a hard-coded shell colour now goes unnoticed; see `CLAUDE.md`'s theme clause.
- **「有没有箭头」 needs `tools/cursor-watch.ps1`, never `shot.ps1`.** A GDI screenshot never contains the cursor. `cursor-watch.ps1` prints `GetCursorInfo` as a timeline and, when the flag says a cursor is showing, draws that cursor into the capture with `DrawIconEx` — so 「有箭头」 and 「没有箭头」 become visible in an image. It never touches z-order, the foreground or the cursor position, which is exactly why `shot.ps1` is the wrong tool here: it raises the window topmost and back, and that changes which queue owns the cursor.
- **`winapp ui` can read a live window without touching it.** `inspect`, `search`, `get-value`, `get-property` and `wait-for` are pure UI-Automation reads — no pointer moves, no clicks — which makes them a safer way to assert what is actually on screen than `poke.ps1`, and `wait-for --value` picks the right pattern per control type by itself. Its interacting verbs are bound by `CLAUDE.md`. Full verb list and the batch-script template are in the `winui-ui-testing` skill.
- Navigate with the app's own switches rather than the mouse, and never start real playback. Both lists, and why the mouse is not an option here, are in `CLAUDE.md`.

## Other scripts

`smoke.ps1` at the repo root starts the app, waits, then reports whether the process is still alive and prints this run's log. In `tools/`: `verify-publish.ps1`, `backup.ps1`, `shortcut.ps1`, `zoom.ps1`, `poke.ps1`, `cursor-watch.ps1`, `line-icons.awk`. Operating detail that isn't a rule — building for ordinary running, `publish.ps1`'s other switches, what the self-check walks through, where the data directory is — is in [`docs/开发与验证.md`](../../docs/开发与验证.md).
