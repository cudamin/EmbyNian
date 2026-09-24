---
name: "embynian-verification"
description: "Verify EmbyNian changes using the current gates in CLAUDE.md. Interpret self-check results, distinguish fresh Release tests from stale output, inspect screenshots and pointer evidence, and report playback coverage gaps. Use after code changes, when editing verification tools, or when asked to 验证 / 跑一遍闸门 / 发新版本."
---

# EmbyNian — verification evidence

**Policy lives in [CLAUDE.md](../../../CLAUDE.md); commands and report paths live in [docs/开发与验证.md](../../../docs/开发与验证.md).** This skill explains what measurements mean, not a second gate schedule. Architecture is `embynian-winui-shell`; playback is `embynian-playback`; shaders are `mpv-shader-quality`.

## Before accepting a result

- Confirm tested files and output belong to the current working tree. `--no-build` needs a successful build of the current test code and Core; `-c Release` alone can select stale output. Publishing the Shell does not refresh the test assembly.
- Report observed counts, failures and skips separately. Missing fixtures, unavailable input and known old failures are not passes.
- An isolated worktree's publish is not delivery to the desktop shortcut. Check the actual target as required by CLAUDE.md.
- The local cursor, motion and composition probes select the integrated pipeline. They do not validate the standalone window, uosc, standalone switching or the external mpv backend.
- Self-check uses copied settings and an isolated run directory, but can still sign in and read the real server. It is not an offline probe. Never inspect or print real tokens to validate redaction; use placeholders.
- Before testing a rule, confirm the chosen probe executes it. In particular, `BeginMotionProbe` has stopped the player ticker; rules inside `OnTick` cannot be inferred from an unmodified motion run that does not tick. If temporary instrumentation is needed, separate its evidence from tests of the final uninstrumented build, and remove it before delivery.
- Helpers under ignored `work/` are local experiments, not clean-checkout prerequisites. Verify their existence and read them before reuse. Mouse probes on the live desktop must verify that their target is this process's window (or the intended desktop) before injecting input; a window covering the target can otherwise receive the click.

## Comparing self-check reports

Run `tools/selfcheck-diff.ps1`. It reads this run's isolated log and compares check names and statuses rather than volatile detail numbers. Non-maximized settings apply to the copy only; never edit and restore the real settings file as a workaround. Concurrent self-checks are rejected; coordinate other desktop-driving verification as well.

`-Log` compares an existing report without launching the app. This proves only what is in that report, not that the current source was built or exercised. Report the source executable and run directory. Missing/incomplete reports and baseline failures are not green.

The [baseline](../../../docs/selfcheck-baseline.txt) can evolve through reviewed additions, renames, merges and retirements under CLAUDE.md. Keep evidence of the old/new coverage and update only the intended entries. Do not use `-UpdateBaseline` to accept a failing run or silently remove someone else's checks.

### Establishing a before/after comparison

The publish script clears old output; it does **not** automatically archive a pre-change build. If a comparison is needed, preserve a complete known pre-change build before replacing it, including native libraries and assets. Record its commit, uncommitted changes and artifact hashes. The previous publish may already contain earlier edits from the same task; different hashes prove different files, not the before/after relationship.

Keep both reports and run under comparable account, server-data and window conditions. Reproduction in the old build is evidence that a symptom existed earlier, not blanket proof that the new change cannot worsen it. A nondeterministic difference needs more evidence than one run per build. If the old executable predates isolated self-check support, do not pass an unknown switch and assume isolation; avoid running it against production data and report that comparison as unavailable until a safe method is established.

Historical diagnoses in PROGRESS.md describe their own runs. They do not waive a current red gate or justify a baseline update. See the development document's “逐行比报告：每次都会变的行” for raw-report interpretation.

### Retired cursor checks

The three ordinary self-checks retired on 2026-09-22 depended on an undisturbed physical mouse: `藏鼠标真的到了系统`, `鼠标真等两秒就藏`, and `藏匿期净位移走不够一百像素不醒`. Do not register or run them as required gates again. This does not retire `--probe-cursor`, `--hide-cursor`, or valid cursor assertions in other checks.

For the isolated cursor probe, preserve the failing assertion and input evidence before retrying. A diagnostic counter or an internal hidden flag is not proof of visible system-cursor behavior. “指针没挪动，只作参考” means missing coverage, not success.

## Moving the pointer, and proving it moved

Navigation and authorization follow CLAUDE.md. These are measurements from this machine, not permanent guarantees; confirm the current target received input.

| Attempt | Observed messages | Pointer movement |
| --- | --- | --- |
| `SendInput` relative move of (0,0) | No `WM_MOUSEMOVE` or `WM_SETCURSOR`, despite return value 1 | No |
| `SetCursorPos` to the existing position | One of each, including from a background process | No |
| `SendInput` with real displacement | Delivery varied between runs | Must measure |
| `SetCursorPos` with real displacement | Coordinate changed, including across monitors | Yes in the measured runs |

- Moving a coordinate does not prove the XAML island received input. A one-pixel `SendInput` out and back has reached the input queue in earlier runs; check pointer-event counts now. `GetCursorInfo` measures the cursor, not input delivery.
- Do not base screenshots on a parked pointer. The user can move the same mouse; hover-only states use the documented display switches.
- Restore pointer position and input-thread attachments when a probe exits. A denied tool is not permission to try another injection mechanism.

## Screenshots — what property checks cannot see

Correct properties do not prove correct pixels. A clipped poster or blank background can pass geometry checks; pair relevant assertions with actual screen evidence.

- `tools/shot.ps1` launches, captures and closes. For settings, use `-WindowTitle 设置` and a non-primary display when available, so the wrong window is not raised over the user's work. Single-monitor handling follows CLAUDE.md.
- An ignored resize helper may support chosen window dimensions, but must be inspected before reuse; do not assume a previous session's scratch file exists.
- `--dump-ui` writes the final page's PNG and visual tree in the isolated run's log directory. It does not composite Mica; use a real screen capture for colors and `shot.ps1` for per-page photography.
- Color changes cover every live theme; spacing/type changes cover the affected page and relevant window sizes. Read the live list from `UiThemes.cs`, not an old screenshot list.
- “有没有箭头” needs cursor evidence: `tools/cursor-watch.ps1` records `GetCursorInfo` and draws the visible cursor into a capture. It does not change foreground, z-order or pointer position. `shot.ps1` raises a window, which can change which input queue owns the cursor, so the tools are not interchangeable.
- Read-only UI Automation can inspect without moving the pointer. In templates, locate the owning item then its local handle; the source scanner skips templates and cannot prove runtime addressability. Interacting verbs remain subject to playback authorization.

## Tool changes

A verification-tool edit needs tests of its affected success and failure paths as well as the applicable gates. `tools/check-scripts.ps1` checks PowerShell encoding/line endings/syntax, not runtime correctness. `smoke.ps1` checks process survival, not successful playback. Keep their command help and messages consistent with the current policy.
