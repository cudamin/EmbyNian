# EmbyNian — Standing Rules for Claude

Read this before you touch anything. **Work still in flight lives in [PROGRESS.md](PROGRESS.md)** — this file holds the durable rules and commands, that one holds what the current phase is about.

This file is in English. **What you produce for the user stays Chinese**: reports, commit messages, UI strings.

## Prime Directive（首要目标）— the user's call, 2026-09-02

**As long as it meets the requirement, the code should be as simple, clear, reliable and maintainable as possible** — not shackled by rules that make no sense. His own words: "I don't really understand code, so I may give bad directions."

**This outranks every other clause in this file, the layering rules included.** How to use it:

- **Technical rules are means, not ends.** Layering, MVVM, DI and interfaces are defaults because they usually do make the code clearer. **Where following one makes a given spot longer, more convoluted or easier to get wrong, take the simpler path** — but say so: which spot, why, what you did instead, plus a line in PROGRESS.md. Deviating silently is as bad as complying blindly.
- **His technical instructions can be argued with.** He said himself he may misdirect, so when he names an implementation you judge would make the code worse, **spend one or two plain sentences on the cost and let him decide**. If he says it again, do it: that is his decision, not a misunderstanding.
- **"Simple" is judged by three things**: how many files you must open to change one behavior, whether a single file can still be read end to end, and whether a decision can be pinned down by a unit test. **Not line count** — the comments explaining *why* the code is the way it is are written for the next window, which arrives with no context; deleting them hands it the maintenance cost.
- **He rules on what he can see; you rule on what he can't.** Every defect actually caught in this project came from him looking at the screen — that is his strength. Which class to use, whether to split a file, whether to add an interface is yours.
- **These sections are not exempt**: the four gates (`-c Release` and single-node build included), no real playback while verifying, credentials, Git, this machine's traps. They constrain process and safety, not the shape of the code, and each was bought with a real incident — routing around them only makes the next regression invisible.

## What This Is（这是什么）

An Emby desktop client. WinUI 3 + Windows App SDK 2.4.0 + .NET 10 + C#, unpackaged, x64; the playback core is libmpv (**don't change `libmpv-2.dll`'s version**). Three projects: `src/EmbyNian.Core` (no NuGet, no UI packages, hence the only layer unit tests reach directly), `src/EmbyNian.Shell` (the WinUI shell), `tests/EmbyNian.Tests` (a console test runner).

## Skills（技能）

Four skills live in **`.claude/skills/`** in this repo, versioned with the code so no app update can replace them: `embynian-winui-shell` (architecture and shell terrain), `embynian-playback`, `embynian-verification` (the gate procedure and screenshot checking), `mpv-shader-quality`. **This file outranks all four** — if one disagrees with a clause here, that skill is the stale one, so fix it in the same change. **Don't add a fifth home**: one skill per subject, in this folder.

**A skill must not restate this file** (his call, 2026-09-05: 「给记忆和技能瘦身」). This file is already loaded whenever a skill fires, so a second copy of a rule buys nothing and goes stale on its own — which is exactly how a skill ends up disagreeing with the clause it was copied from. The gate commands, the SDK path, the switch list, credentials, git and how to report live **here only**; a skill carries what is specific to its subject and points back here for the rest.

## Layering（分层规则）— the user's call, 2026-08-24

Subordinate to the prime directive above — the two rules below are defaults, not untouchable law.

1. **Pure UI behavior may stay in code-behind.** That covers `Visibility` toggles, `Frame` navigation and the back stack, hand-built `NavigationViewItem`/`MenuFlyoutItem`, window buttons, and anything that needs to know *which element* the event happened on. Why: `MenuFlyout` has `Items` and no `ItemsSource`, and `NavigationView` loses its separators once you switch to `MenuItemsSource` — forcing data binding onto these only makes them worse.
2. **Business capability always goes through a Service.** MVVM throughout; view models get capability from services; services live in the DI container; CommunityToolkit.Mvvm for the ordinary MVVM plumbing; extra abstractions only where genuinely needed.

How that lands in practice — one line each. **The reasoning, the incidents that bought each rule and the terrain behind them (which types, which interfaces, the live examples) are in the `embynian-winui-shell` skill, which fires on any C# or XAML edit here; read it before touching either.**

- Capability lives in `src/EmbyNian.Core`, is registered in `src/EmbyNian.Shell/Composition/ShellServices.cs`, then injected. **Judge by "which project, and is it in the container" — never by directory name.**
- **A judgment with one right answer for a given input becomes a named pure function in Core, pinned by a test** — even when it isn't a "service". Unit tests only reach Core, so a judgment left in a view model is one nobody is watching.
- **No interface just because a class goes into DI.** The exception is narrowing: `ISettingsService` and `IServerCapabilities` are interfaces on purpose — don't "clean" them into concrete classes.
- **Pages build their own view models and receive dependencies through `Attach(...)`**, with a ring of "not attached yet, return early" guards; that isn't a shortcut, write it that way. **Only `PlayerViewModel` is in the container**, because a movie keeps playing behind the library page.
- Code-behind holds no business logic, no HTTP/Emby calls, no playback state, no persistence. `Views/ItemCommands.cs` is the one exception, and it is old debt rather than a template.
- `View → ViewModel → Service → outside world` is a direction, not a chain every file completes; models, converters, small controls and helpers with no business dependency stay simple.
- Reuse the existing Service / ViewModel / Core types rather than extracting a `Manager`/`Helper`/`Factory`/`IWhatever` — which does not soften the "judgments into Core" rule above.

## The Four Gates（四道闸门）— run all four after any code change

The `dotnet` on `PATH` is unusable: it's 8.0.403 and this project needs .NET 10. The SDK is at `%USERPROFILE%\.dotnet\dotnet.exe` and is not on `PATH`. **Build single-node** — this machine's Windows SDK multi-node workload resolver makes parallel builds fail intermittently with no output at all.

1. Build

   ```
   %USERPROFILE%\.dotnet\dotnet.exe build .\EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
   ```

2. Test

   ```
   %USERPROFILE%\.dotnet\dotnet.exe run --project .\tests\EmbyNian.Tests\EmbyNian.Tests.csproj -c Release --no-build
   ```

   **`-c Release` is not optional.** `dotnet run` looks for Debug output by default, so with `--no-build` it runs whatever stale binary sits in `bin\Debug` and still prints 「全部通过」 with exit code 0 — when this was caught on 2026-08-31 that binary was 160 tests short of the source. Dropping the switch switches this gate off.

3. Publish: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive`
4. Self-check: `artifacts/publish/win-x64/EmbyNian.exe --self-check --dump-ui`, then read `%LOCALAPPDATA%\EmbyNian\logs\selfcheck-shell.txt` — it needs exit code 0 and 「结果：全部通过」 on the last line.

A few things about the gates:

- **The self-check opens on the secondary monitor by default** so it won't steal the screen in use; add `--screen 1` to watch it run, and that default lapses by itself on a single-monitor machine.
- Ten lines of the report differ every run by design; only those changing is not a regression. The list is in [docs/开发与验证.md](docs/开发与验证.md) under 「逐行比报告：每次都会变的行」 and that is its only live copy.
- The passing-test count keeps growing as development goes on. **Never write a specific number into a document or a memory** — it will go stale.
- **The UI can be photographed**: `tools/shot.ps1` launches, shoots and closes, `--dump-ui` leaves a shot plus a visual tree, and the self-check leaves one of its own. The procedure, and which themes to shoot, are in the `embynian-verification` skill.
- Touched colors → one shot per theme. The six live in `src/EmbyNian.Core/Theming/UiThemes.cs` (`emby-dark` default, `oled-black`, `midnight`, `graphite`, `plum`, `daylight`); `daylight` is the only light one, and hard-coded shell colors plus the system-drawn title bar only show themselves there.

## No Real Playback While Verifying（验证时不要真实播放）

The live Emby server (192.168.31.230:8896) is the user's real library, not a fixture — one real playback writes into watch history and resume points.

- **Don't click the middle of a card**: that's the play button floating up on hover, `{ESC}` will not stop what it starts and only killing the process will. Reach a detail page with `--show-detail` / `--show-episode`, or via the home hero's 「详情」 button, the breadcrumb, or a card's bottom title strip — never the artwork itself.
- **Navigate with command-line switches, never the mouse** — `tools/poke.ps1` clicks wherever the cursor happens to be sitting. What programmatic pointer movement actually does here was re-measured on 2026-09-05 with a message-counting window parked under the cursor, because the old blanket 「it does nothing」 was what made 「ask the OS about the cursor again」 get written as an injection that never fired: **a `SendInput` relative move of (0,0) produces nothing at all** — 0 `WM_MOUSEMOVE`, 0 `WM_SETCURSOR`, return value 1 either way — while **`SetCursorPos` to the point the cursor already occupies produces one of each, and does so from a process that is not in the foreground**. A `SendInput` move with a real displacement moved the pointer in one run and not in another, so don't lean on it.
- `--play` is the only switch that really starts playback.

## Credentials（凭据）

The Emby access token is stored DPAPI-wrapped. **Never print it, never log it, never let it into the self-check report.** To hand it to a web view use `localStorage`, not a query string. The token's occurrence count in the self-check report must be 0.

## Git

- `origin` is `https://github.com/cudamin/EmbyNian.git`, a **private** repository. Credentials are held by this machine's Git Credential Manager; keep tokens out of commands, scripts and remote URLs.
- **One long-lived branch, `master`**, with HEAD on it (it was `winui3-rewrite` until 2026-09-02, so anything in the history under that name predates the rename). **Don't create a second branch that follows the trunk** — there used to be one and it bought nothing but an extra push. Push with `git push origin master`, no `git push . <branch>:<branch>` ref-shuffling.
- **Never reset, check out over, or roll back the migration work in the working tree** (the whole WinForms → WinUI 3 change).
- Commit only when the user explicitly asks. Commit messages: **Chinese body** plus `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Source files are LF, always.
- Push to `origin` right after committing, no need to ask again (his call, 2026-09-01). A push only ever follows a commit — uncommitted working-tree changes are neither committed nor uploaded.

## Traps On This Machine（这台机器上的坑）

- bash resets the working directory on every call → prefix commands with `cd "C:/Users/89400/EmbyNian" &&`.
- There is no `python`.
- PowerShell scripts must be saved as **UTF-8 with BOM**, or Chinese output comes out as mojibake.
- A custom `cut()` function in the shell shadows `/usr/bin/cut`; truncate with `awk '{print substr($0,1,N)}'` instead.
- **The mouse cursor cannot be photographed** — a GDI screenshot never contains it. `tools/cursor-watch.ps1` is the way round that, and `tools/shot.ps1` cannot be used for it; why, and how to read its output, are in the `embynian-verification` skill.
- Command-line switches, all of them: `--dump-ui`, `--hide-cursor` (parks the player on screen with its 10 Hz ticker running and nothing else, so the pointer's two-second auto-hide can be watched — plays nothing), `--maximized`, `--play`, `--screen`, `--scroll-end`, `--scroll-half`, `--self-check`, `--show-detail`, `--show-episode`, `--show-library`, `--show-menu` (pops the first home card's 更多 menu open), `--show-osd` (parks the player's overlay on screen without playing a byte; optionally `pinned`, `paused` or `playing`), `--show-settings` (optionally a category, e.g. `--show-settings 关于`; defaults to 「界面」), `--theme`. **`--hide-cursor`, `--show-menu` and `--show-osd` are each used on their own, never with `--self-check` and never with each other.**

## How To Report（怎么汇报）

The user doesn't read code: settle the implementation details yourself, verify them yourself, and **report in Chinese, in plain words, with no code pasted in**. The only things worth asking about are the ones genuinely his — scope, priority, user-visible behavior, irreversible operations, and **a rule that is currently making this piece of code worse** (see the prime directive). Give him a list he can pick from, not a menu of technical options.
