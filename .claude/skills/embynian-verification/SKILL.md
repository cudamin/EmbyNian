---
name: "embynian-verification"
description: "Verify EmbyNian changes using the current gates in CLAUDE.md. Interpret self-check results, distinguish fresh Release tests from stale output, inspect screenshots and pointer evidence, and report playback coverage gaps. Use after code changes or when asked to 验证 / 跑一遍闸门 / 发新版本."
---

# EmbyNian — verification evidence

**When to run each gate, the exact commands, SDK selection, working-tree and delivery rules, theme coverage, and playback authorization live in [CLAUDE.md](../../../CLAUDE.md).** Use that policy rather than a second schedule here. This skill explains what the measurements mean and where a green result is insufficient. Architecture is `embynian-winui-shell`; playback is `embynian-playback`; shaders are `mpv-shader-quality`.

## Before accepting a result

- Confirm the tested files and output belong to the current working tree. A test run needs a successful build of the current test code and Core, not merely `-c Release`: `--no-build` can run stale Release output too. Publishing the Shell does not refresh the test assembly.
- Report counts observed on this run, failures and skips separately. Do not turn a missing fixture or unavailable UI input into a pass.
- A publish in an isolated worktree is not delivery to the existing desktop shortcut. Verify the actual delivery path using the rule in CLAUDE.md.
- The local cursor, player-motion and composition probes currently select the integrated pipeline. Their success does not validate the standalone window, uosc or standalone source switching. Report that gap when those features change.
- `--screen` makes a diagnostic run independent of saved window placement. Normal self-check can use a saved account and read server data; it is not the isolated offline playback path.
- **`--probe-player-motion` 的 `BeginMotionProbe` 会停掉播放页的轮询计时器**（`PlayerPage.MotionProbe.cs` 里的 `_ticker.Stop()`），而**任何住在 `OnTick` 里的判据在那个探针里都不会跑** —— 每拍前台位（`WakeClick` 那条「叫醒窗口的那一下不作数」要它）、光标规则都是。2026-09-23 验这条时因此白跑三趟，最后靠临时加一个「把轮询开回来」的探针口子（跑完删）才拿到读数。要在运动探针里验这类东西，先临时开轮询，别把「判据没触发」当成「判据不成立」。
- **点画面的手势有两处真鼠标探针**：`work/probe-activation-click.py`（独占：真 mpv 窗口 + uosc 探针拷贝，`focused`/键位等级/押后提交撤销都有读数）与运动探针里那段临时装置（集成：真页面的三行点击日志 ＋ 把前台交给别的窗口）。两者都在**用户的活桌面**上合成鼠标，落点被别的窗口盖住时那一发会点进他正在用的窗口里 —— 点前必须问一句「指针底下是不是本进程的窗口/桌面」（探针里已加）。

## Comparing self-check reports

Use `tools/selfcheck-diff.ps1`; its acceptance criteria and baseline-update policy are in CLAUDE.md. It compares check names and statuses, not changing numbers inside details, and handles non-maximized placement for its run before restoring settings. **Do not manually cross out failures to manufacture a green report.**

When investigating a raw report, the explanation of volatile readings is in [docs/开发与验证.md](../../../docs/开发与验证.md), under “逐行比报告：每次都会变的行”. The baseline is [docs/selfcheck-baseline.txt](../../../docs/selfcheck-baseline.txt). Keep that explanation there rather than duplicating the list.

**To prove a red is not yours, run the previous published build against it.** Every republish moves `artifacts/publish` to `C:\Users\89400\EmbyNian-stale\publish-<stamp>`, and each stale copy is a complete self-contained build (its own `libmpv-2.dll` and assets) made from the tree *before* the change in hand. Launch it straight from there and let it write its own report over `%LOCALAPPDATA%\EmbyNian\logs\selfcheck-shell.txt` — copy the current report into `work\` first, and compare the two `EmbyNian.dll` SHA256 to prove they really are different binaries:

```powershell
$env:Path = 'C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem'
$p = Start-Process -FilePath '<stale>\win-x64\EmbyNian.exe' -ArgumentList '--self-check' -PassThru
$null = $p.WaitForExit(300000)
```

Measured 2026-09-21: after a home-page change, two reds appeared (`播放回来那一页`、`服务器页面`). The pre-change build reproduced `播放回来那一页` **with a character-identical reading** (`页面滚在 891`), and the third red slot swapped between two unrelated checks across the two runs — a pre-existing red plus a timing-sensitive one, neither caused by the change. Only a red that is absent before and present after is yours to chase. Note this also shows the baseline (captured at 16:24 that day) had drifted from the working tree: do not `-UpdateBaseline` to make someone else's in-flight work look green.


**When “鼠标真等两秒就藏” fails, start with `不符：`, not the diagnostic counters.** The first poll establishes a pointer baseline, so a movement count of 1 can describe a completely undisturbed leg. The probe checks disturbance using both movement count and final position; a genuinely disturbed leg says “这一轮只作参考”. Save the failed assertion and “空事件” count before retrying, because an unexpected un-hide can arrive as a XAML event.

Two checks exercise different cursor failures: “藏好后挪一个像素：过后还藏着” rejects waking from tiny displacement, while “挪一下就回来” requires a real displacement to reveal it. A result saying “指针没挪动，这一句只作参考” is a coverage limitation, not proof of the first behavior. Internal hidden state is not a substitute for the visible system cursor.

## Moving the pointer, and proving it moved

Navigation and authorization follow CLAUDE.md. The measurements below were made on this machine on 2026-09-05; use them to choose a probe, then verify that the current run actually delivered its input.

| Attempt | Observed messages | Pointer movement |
|---|---|---|
| `SendInput` relative move of (0,0) | No `WM_MOUSEMOVE` or `WM_SETCURSOR`, despite return value 1 | No |
| `SetCursorPos` to the existing position | One of each, including from a background process | No |
| `SendInput` with a real displacement | Delivery varied between runs | Must measure |
| `SetCursorPos` with a real displacement | Coordinate changed, including across monitors | Yes in the measured runs |

- **Moving the coordinate is not proof that the XAML island received input.** `SetCursorPos` can move it while the island sees no pointer events. A one-pixel `SendInput` out and back has reached the input queue and made WinUI re-read `ProtectedCursor`; check the island's pointer-event count to prove delivery. `GetCursorInfo` answers about the cursor, not input delivery.
- **Do not base a screenshot on a parked pointer.** The user can move the same mouse before capture. Hover-only UI uses the state switches in CLAUDE.md.
- Restore pointer position and input-thread attachments when a probe exits. Permission denial is not an invitation to try a different injection mechanism.

## Screenshots — what property checks cannot see

A correctly reported property does not prove correct pixels. Prior failures included a clipped poster and a background layer with no visible content despite passing geometry checks. Pair the relevant property assertions with actual screen evidence.

- **`tools/shot.ps1`** launches, captures and closes. For the settings window, select a non-primary screen when available and pass `-WindowTitle 设置`; otherwise it can raise the wrong window over the user's work. Screen choice follows CLAUDE.md on single-monitor machines.
- **Chosen window size:** the local `work/shot-resize.ps1` helper has been used with `-ResizeWidth/-ResizeHeight`. `work/` is not tracked, so check it exists and read it before reuse; it is not a clean-checkout prerequisite. Respect the current tools' permissions rather than relying on a previous sandbox's behavior.
- **`--dump-ui`** writes `selfcheck-shell.png` and a visual tree after the last self-check page. It does not composite Mica, so use a real screen capture to judge colors. Per-page photography is `shot.ps1`'s job.
- **Theme coverage follows CLAUDE.md.** For spacing or type-size changes without color changes, inspect the affected page and relevant window sizes. Read the live theme list from `UiThemes.cs`; do not resurrect a deleted theme just for a screenshot.
- **“有没有箭头” needs cursor evidence.** `tools/cursor-watch.ps1` reads `GetCursorInfo` over time and uses `DrawIconEx` to put a visible cursor into a capture. It does not change z-order, foreground or pointer position. `shot.ps1` raises the window, which can change the input queue that owns the cursor, so it is not interchangeable with this test.
- **Read-only UI Automation** (`inspect`, `search`, `get-value`, `get-property`, `wait-for`) can inspect a running window without moving the user's pointer. For templated controls, locate the owning item before a local handle; the source scanner skips these controls and cannot prove runtime addressability. Interacting verbs remain subject to CLAUDE.md's safety rules.

## Other scripts

`smoke.ps1` starts the app, waits and reports whether it is alive; survival alone does not validate playback. Helpers under `tools/` include `verify-publish.ps1`, `backup.ps1`, `shortcut.ps1`, `zoom.ps1`, `poke.ps1` and `cursor-watch.ps1`. Parameters, data paths and detailed report interpretation belong in [docs/开发与验证.md](../../../docs/开发与验证.md).
