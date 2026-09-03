# EmbyNian — Standing Rules for Claude

Read this before you touch anything. **Work still in flight lives in [PROGRESS.md](PROGRESS.md)** — the two files split the job: this one holds the durable rules and commands, that one holds what the current phase is about.

This file is in English. **What you produce for the user stays Chinese**: reports, commit messages, UI strings.

## Prime Directive（首要目标）— the user's call, 2026-09-02

**As long as it meets the requirement, the code should be as simple, clear, reliable and maintainable as possible** — not shackled by rules that make no sense. His own words included: "I don't really understand code, so I may give bad directions."

**This outranks every other clause in this file, the layering rules included.** How to use it:

- **Technical rules are means, not ends.** Layering, MVVM, DI and interfaces are the defaults because they usually do make the code clearer. **Where following one makes a given spot longer, more convoluted or easier to get wrong, take the simpler path** — but say so: which spot, why, what you did instead, plus a line in PROGRESS.md. Deviating silently is as bad as complying blindly.
- **His technical instructions can be argued with.** He said himself he may misdirect. So when he names an implementation and you judge it would make the code worse, **spend one or two plain sentences on the cost, then let him decide** — don't just comply, and don't quietly fail to. If he says it again, do it: at that point it's his decision, not a misunderstanding.
- **"Simple" is judged by three things**: how many files you must open to change one behavior, whether a single file can still be read end to end, and whether a decision can be pinned down by a unit test. **Not line count** — the comments explaining *why* the code is the way it is are a large share of the source, written for the next window that arrives with no context. Deleting them isn't simplification, it's handing the maintenance cost to the next person.
- **He rules on what he can see; you rule on what he can't.** Every defect actually caught in this project came from him looking at the screen: a strip cropped off a poster, an empty patch in the top-left corner, a black border around the episode list. That's his strength. Which class to use, whether to split a file, whether to add an interface — that's yours, don't take it to him.
- **These sections are not exempt**: the four gates (`-c Release` and single-node build included), no real playback while verifying, credentials, Git, this machine's traps. They constrain process and safety, not the shape of the code, and each was bought with a real incident — routing around them won't make the code simpler, it will only make the next regression invisible.

## What This Is（这是什么）

An Emby desktop client. WinUI 3 + Windows App SDK 2.4.0 + .NET 10 + C#, unpackaged, x64; the playback core is libmpv (**don't change `libmpv-2.dll`'s version**). Three projects: `src/EmbyNian.Core` (no NuGet, no UI packages, hence the only layer unit tests reach directly), `src/EmbyNian.Shell` (the WinUI shell), `tests/EmbyNian.Tests` (a console test runner).

## Layering（分层规则）— the user's call, 2026-08-24

Subordinate to the prime directive above — the two rules below are defaults, not untouchable law.

1. **Pure UI behavior may stay in code-behind.** That covers `Visibility` toggles, `Frame` navigation and the back stack, hand-built `NavigationViewItem`/`MenuFlyoutItem`, window buttons, and anything that needs to know *which element* the event happened on. Why: `MenuFlyout` has `Items` and no `ItemsSource`, and `NavigationView` loses its separators once you switch to `MenuItemsSource` — forcing data binding onto these only makes them worse.
2. **Business capability always goes through a Service.** MVVM throughout; view models get capability from services; services live in the DI container; CommunityToolkit.Mvvm for the ordinary MVVM plumbing; extra abstractions only where genuinely needed.

How that lands in practice (re-checked against the code on 2026-09-02):

- **Capability lives in `src/EmbyNian.Core` and is registered in `src/EmbyNian.Shell/Composition/ShellServices.cs`** (`ValidateOnBuild = true`), then injected. Most of it sits in `Core/Emby`, `Core/Playback` and `Core/Configuration` (session, Emby client, image cache, playback, credentials, settings store); `Core/Services/` holds only the handful carved out later. **Judge by "which project, and is it in the container" — never by directory name.** Moving files to match a directory name changes nothing.
- **Judgments with a computable answer belong in Core too, even when they aren't a "service".** Which rows the home page shows, which monitor the window lands on, whether two batches of images are the same batch, how large a poster cell is, which cached images to evict — anything with exactly one right answer for a given input becomes a pure function in Core, pinned by the test project. **The reason is coverage**: unit tests only reach Core, so a judgment left in a view model is one nobody is watching. The `HomeLayout.Plan` hole that permanently discarded the user's row order and checkboxes came from exactly that — not one gate went red, a screenshot caught it.
- **Don't invent an interface just because a class goes into DI**; with no second implementation and nothing to substitute or isolate, register the concrete class. **The exception is narrowing**: `ISettingsService` and `IServerCapabilities` have one implementation each and no test doubles, and they exist so a page that only wants "items per page" can't reach the session, the image cache and the player. **Don't "clean" those two into concrete classes** — that spreads everything back within arm's reach.
- **Pages construct their own view models; dependencies arrive afterwards through `Attach(...)`.** A WinUI page must have a parameterless constructor and is built by the framework rather than the container, so constructor injection into pages is a dead end. Every view model except the player's looks like this; the price is nullable service fields plus a ring of "not attached yet, return early" guards — that isn't a shortcut, write it that way. **Only `PlayerViewModel` is in the container**: a movie keeps playing behind the library page, so its state has to outlive any navigation.
- Code-behind holds no business logic, no HTTP/Emby calls, no playback state, no persistence. **One known exception: `Views/ItemCommands.cs`** (the card context menu — watched, favorite, edit metadata) holds an `EmbySession` and issues requests itself, isn't in the container, and has no tests of its own. The field rules for editing have since moved into `Core/Emby/ItemMetadataEdit.cs` and are pinned there; the request plumbing is what's still owed. **Old debt, not a template — don't copy the shape.**
- `View → ViewModel → Service → outside world` is a direction, not a chain every file has to complete. Models, converters, small controls and helpers with no business dependency stay simple.
- Before you start, read the existing Service / ViewModel / Core types and the DI registrations, and reuse what's there; don't mechanically extract a `Manager`/`Helper`/`Factory`/`IWhatever`. **This does not govern the "judgments into Core" rule above** — those are named pure functions with tests holding them down, not a shell wrapped around something that already exists.

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

   **`-c Release` is not optional.** `dotnet run` looks for Debug output by default, so with `--no-build` it runs whatever stale binary sits in `bin\Debug` and still prints 「全部通过」 with exit code 0. When this was caught on 2026-08-31 that binary was two days old and 160 tests short of the source. Dropping the switch is the same as switching this gate off.

3. Publish: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive`
4. Self-check: `artifacts/publish/win-x64/EmbyNian.exe --self-check --dump-ui`, then read `%LOCALAPPDATA%\EmbyNian\logs\selfcheck-shell.txt` — it needs exit code 0 and 「结果：全部通过」 on the last line.

A few things about the gates:

- **The self-check opens on the secondary monitor by default** so it won't steal the screen in use; add `--screen 1` to watch it run, and that default lapses by itself on a single-monitor machine.
- About a dozen lines of the report differ every run by design (timestamps, window and cursor handles, thread ids, timings, sampled colors, server-side counts, line counts). Only those changing is not a regression; the full list is in PROGRESS.md.
- The passing-test count keeps growing as development goes on. **Never write a specific number into a document or a memory** — it will go stale.
- **The UI can be photographed**: `tools/shot.ps1` (`-Exe … -ExeArgs "--theme daylight --show-settings" -SettleMs N`, which launches, shoots and closes) and `--dump-ui` are both there, and the self-check leaves a shot of its own.
- Touched colors → one shot per theme. The six live in `src/EmbyNian.Core/Theming/UiThemes.cs` (`emby-dark` default, `oled-black`, `midnight`, `graphite`, `plum`, `daylight`); `daylight` is the only light one, and hard-coded shell colors plus the system-drawn title bar only show themselves there.

## No Real Playback While Verifying（验证时不要真实播放）

The live Emby server (192.168.31.230:8896) is the user's real library, not a fixture — one real playback writes into watch history and resume points.

- **Don't click the middle of a card**: that's the play button floating up on hover, clicking it starts playback, `{ESC}` will not stop it and only killing the process will. Reach a detail page with `--show-detail` / `--show-episode`, or via the home hero's 「详情」 button, the breadcrumb, or the title strip along the bottom of a card — never the artwork itself.
- **Moving the mouse programmatically does nothing on this machine** (`SendInput` returns 1 and the cursor stays put; `SetCursorPos` likewise), so `tools/poke.ps1` clicks wherever the cursor happens to be sitting. Navigate with command-line switches, never the mouse.
- `--play` is the only switch that really starts playback.

## Credentials（凭据）

The Emby access token is stored DPAPI-wrapped. **Never print it, never log it, never let it into the self-check report.** To hand it to a web view use `localStorage`, not a query string. The token's occurrence count in the self-check report must be 0.

## Git

- `origin` is `https://github.com/cudamin/EmbyNian.git`, a **private** repository. Credentials are held by this machine's Git Credential Manager; keep tokens out of commands, scripts and remote URLs.
- **One long-lived branch, `master`**, with HEAD on it. It was `winui3-rewrite` (a leftover name from the WinForms → WinUI 3 rewrite) until the user renamed it on 2026-09-02 and the old name left the remote the same day, so anything in the history mentioning `winui3-rewrite` is talking about before the rename.
- **Don't create a second branch that follows the trunk.** There used to be a mirror branch following it unconditionally; it's gone, since it bought nothing but an extra fast-forward and push after every commit. Push with `git push origin master` — no `git push . <branch>:<branch>` ref-shuffling either.
- **Never reset, check out over, or roll back the migration work in the working tree** (the whole WinForms → WinUI 3 change).
- Commit only when the user explicitly asks. Commit messages: **Chinese body** plus `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Source files are LF, always.
- Push to `origin` right after committing, no need to ask again (his call, 2026-09-01). There is no scheduled job: a push only ever follows a commit, and uncommitted working-tree changes are neither committed nor uploaded.

## Traps On This Machine（这台机器上的坑）

- bash resets the working directory on every call → prefix commands with `cd "C:/Users/89400/EmbyNian" &&`.
- There is no `python`.
- PowerShell scripts must be saved as **UTF-8 with BOM**, or Chinese output comes out as mojibake.
- A custom `cut()` function in the shell shadows `/usr/bin/cut`; truncate with `awk '{print substr($0,1,N)}'` instead.
- Command-line switches, all of them: `--dump-ui`, `--hide-cursor` (parks the player on screen with its 10 Hz ticker running and touches nothing else, so the mouse pointer's two-second auto-hide can be watched — plays nothing; don't combine it with `--self-check` or `--show-osd`), `--maximized`, `--play`, `--screen`, `--scroll-end`, `--scroll-half`, `--self-check`, `--show-detail`, `--show-episode`, `--show-library`, `--show-menu` (pops the first home-page card's 更多 menu open, for a screenshot — use it on its own), `--show-osd` (parks the player's overlay — transport bar, title strip, volume rail — on screen without playing a single byte, for a screenshot; optionally followed by `pinned`, `paused` or `playing`; use it on its own and never with `--self-check`), `--show-settings` (optionally followed by a category name, e.g. `--show-settings 关于`; defaults to 「界面」), `--theme`.
- **The mouse cursor cannot be photographed, and a GDI screenshot never contains it.** `tools/cursor-watch.ps1` is the way round that: it prints `GetCursorInfo` as a timeline and, when the flag says a cursor is showing, draws that cursor into the capture with `DrawIconEx` — so 「there is an arrow」 and 「there is none」 become visible in an image. It never touches z-order, the foreground or the cursor position, which is exactly why `tools/shot.ps1` cannot be used for this (it raises the window topmost and back, and that changes which queue owns the cursor).

## How To Report（怎么汇报）

The user doesn't read code: settle the implementation details yourself, verify them yourself, and **report in Chinese, in plain words, with no code pasted in**. The only things worth asking about are the ones genuinely his — scope, priority, user-visible behavior, irreversible operations, and **a rule that is currently making this piece of code worse** (see the prime directive). Give him a list he can pick from, not a menu of technical options.
