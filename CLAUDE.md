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

Style is recorded in [`.editorconfig`](.editorconfig) — file-scoped namespaces, no `this.`, `_camelCase` private fields, `PascalCase` const and static readonly, LF, 4 spaces. It was written on 2026-09-05 from a measurement of the tree rather than from a preference, so its `warning` severities are rules all 230 `.cs` files already pass.

## Skills（技能）— the `winui` set is the standard

**Priority order, highest first.** Established 2026-09-05 after he said it three times, the last as 「新技能不要改，以后统一按照新的 winui 技能来」:

1. **The `winui-*` third-party skills, on WinUI 3 practice** — where one of them disagrees with something here, the skill is right and this file is what gets corrected.
2. **This file**, on everything else: the user's own calls, this machine, his server, process and safety.
3. **The four skills in `.claude/skills/`**, on their own subjects.

**「除非你有更好写法」— his call, 2026-09-06**, which replaces the earlier 「no exception list」: 「我要求以后按照 winui 技能执行，除非你有更好写法」. Clause 1 is now a strong default rather than an absolute — the same shape the prime directive already gives the layering rules, pointed at a skill. Follow the skill. Deviate only where you can show the alternative is better on the prime directive's own three tests, and then **say so**: which spot, why, what you did instead, a line in PROGRESS.md, and an entry under 「Where this project writes its own rule」 below if it is durable. Two things this is not:

- **Not licence to skip a rule because following it is work.** 「更好写法」 means a writing you can defend, not an unwritten one.
- **A skill rule nobody has got to yet is not a deviation** — it is owed work, and it belongs in PROGRESS.md under that name. Recording it as a deviation is how an undone job stops looking undone.

### The third-party skills

Five plugins at user scope, in two batches. **The WinUI and media batch, 2026-09-05** — `winui@win-dev-skills` 0.3.0 (microsoft/win-dev-skills, eight skills) and `watch@claude-video` 0.2.0 — plus three folders copied out of `damionrashford/media-os` (`ffmpeg-hdr-color`, `ffmpeg-probe`, `hdr-dovi-tool`). The seven `winui-*` skills are also reachable from `%USERPROFILE%\.claude\skills` as symlinks into `%USERPROFILE%\.cc-switch\skills`, and `%USERPROFILE%\.zcode\skills` holds the same eleven as plain folders for ZCode. **Both plugins show as `disabled`** and that is not a fault: their skills arrive through the symlinked folder instead, so enabling them would load each one twice.

**The .NET batch, 2026-09-06 — three plugins, 12 skills, ~1,600 tokens on every session.** `dotnet-advanced` 0.2.2 and `dotnet-diag` 0.1.1 out of `dotnet/skills` (whose marketplace name is `dotnet-agent-skills`), plus `skill-authoring` 1.4.0 out of `fvadicamo/dev-agent-skills`. The one actually wanted is **`dotnet-pinvoke`** inside `dotnet-advanced` — callback rooting, `SafeHandle`, struct-size asserts, i.e. exactly the libmpv interop surface; the rest is the performance/diagnostics and skill-writing side. **A plugin's skills are named `plugin:skill`** (`dotnet-advanced:dotnet-pinvoke`), and they load into a running session without a restart — looking for the bare name is how 「技能没有增加」 got reported on a session that already had all of them. `dotnet-diag`'s agent (`optimizing-dotnet-performance`) **does load** despite `claude plugin details` printing 「Agents (0)」; that count is the command's own display bug, not a failure.

**What was left out on purpose, so it doesn't get installed again.** Two of them turn on one fact: **`dnx` is not on `PATH` here.** It exists and runs — `%USERPROFILE%\.dotnet\dnx.cmd`, off the 10.0.400 SDK, measured 2026-09-06 — but `PATH` carries `%USERPROFILE%\.dotnet\tools` and not `%USERPROFILE%\.dotnet`, so a bare `dnx` resolves to nothing. **Putting that directory on `PATH` is the fix nobody is to take**: it would make a bare `dotnet` resolve to 10.0.400 and quietly undo the full-path discipline the four gates depend on. So the marketplace's main `dotnet` plugin stays out, because its C# language server launches through `dnx`; and **`dotnet-skills` 0.14.2 out of `richlander/dotnet-skills` was installed and removed the same day**, because both of its skills are bootstrappers that shell out to `dnx dotnet-inspect` — a shell with the knowledge in a tool it cannot reach. Its marketplace entry went with it. `guardrails` is a `PreToolUse` hook and `privacy-guard` wants Python, both from `dev-agent-skills`. **`github-workflow` was also installed and removed the same day**: its `git-commit` enforces Conventional Commits with a mandatory kebab-case scope, against the Chinese-body rule in the Git section below, and its three PR skills have no workflow to attach to on a one-branch private repo. That last removal is the precedent for where this batch sits in the order above — **clause 3, not clause 1: where one of them collides with this file, this file wins**, because none of them is a WinUI-practice skill.

**None of them is ever to be edited** — a plugin update overwrites the edit, so anything that has to hold gets written here instead. `winui-wpf-migration` is the one in the plugin we do not use: this app came from WinForms and that move is finished. All three media-os folders **do run** under the `py` launcher (see the `python` trap below); only `hdr-dovi-tool` is still missing its external `dovi_tool`.

### Our four

`embynian-winui-shell` (architecture and shell terrain), `embynian-playback`, `embynian-verification` (the gate procedure and screenshot checking), `mpv-shader-quality`. They live in **`.claude/skills/`** in this repo, versioned with the code so no app update can replace them. **Don't add a fifth home**: one skill per subject, in this folder.

**A skill must not restate this file** (his call, 2026-09-05: 「给记忆和技能瘦身」). This file is already loaded whenever a skill fires, so a second copy of a rule buys nothing and goes stale on its own — which is exactly how a skill ends up disagreeing with the clause it was copied from. The gate commands, the SDK path, the switch list, credentials, git and how to report live **here only**; a skill carries what is specific to its subject and points back here for the rest.

### Four facts no skill can know

These are **not** exceptions to the priority order above — they are facts about this machine and this user's server.

- **The SDK is `%USERPROFILE%\.dotnet\dotnet.exe`**, and the 8.0.403 on `PATH` cannot build a `net10.0` project. `winui-setup` will read 「8.0.100+ is present」 off it and pronounce the toolchain healthy — a correct check with a wrong input, so don't run it to 「repair」 anything here.
- **Build single-node.** This machine's workload resolver, measured. `winapp run` takes `-p Name=Value` for MSBuild *properties*, so gate 1's three `-p:` flags could go through it but `-m:1` cannot; that is why the gate is a `dotnet build`, and why the analyzer is wired in by hand (see gate 1).
- **Never start real playback while verifying, and never click the middle of a card** — that is the play button, and the server behind it is his real library. This is about his watch history rather than about WinUI, so it binds `winapp ui`'s `click` / `invoke` / `hover` / `drag` and `winui-ui-testing`'s 「exercise every element in one pass」 template exactly as it binds `tools/poke.ps1`. The read-only verbs (`inspect`, `search`, `get-value`, `wait-for`) and the screenshot checklist are welcome as written.
- **Package versions stay pinned**, against 「never pass `--version` to `dotnet add package`」: WindowsAppSDK is split into sub-packages with `Runtime` at `[2.4.0]` to keep ~39 MB of onnxruntime out, and `libmpv-2.dll` doesn't move. This one is code shape and so it is mine to call rather than a standing exception — say so to have it revisited.

### Where this project writes its own rule（更好写法）

Three of them, each measured. Anything not on this list follows the skill.

**`WUI2010` is off, in `src/EmbyNian.Shell/EmbyNian.Shell.csproj` only** — twenty nested `x:Bind` paths the analyzer says will crash, which measurement says cannot: the generated code null-checks every segment, nineteen intermediates are null only between construction and `Attach` and never again, and the twentieth is a non-nullable get-only property. Both fixes the rule offers cost more than they buy. **The reasoning, and what would make the suppression wrong, is in that csproj beside the `NoWarn` — read it there before adding a twenty-first.** This is the only analyzer rule turned off; every other `WUI####` is a real finding.

**`x:Name` is this project's automation handle wherever one already exists.** `winui-code-review` asks for `AutomationProperties.AutomationId` on every interactive control; WinUI reports `x:Name` as the UIA AutomationId when no explicit one is set, so the 67 controls that carry an `x:Name` are already addressable and a second attribute on them would be duplication that can drift. **Measured 2026-09-06, not assumed** — `winapp ui inspect` against the running app reported `automationId=PaneButton`, `SettingsButton`, `PlayButton` and the rest straight off their `x:Name`. So the rule here is: an interactive control needs a **handle**, and `x:Name` counts. Where a control has neither — `x:Uid` is not a handle, it only feeds the resource loader — it gets an explicit `AutomationProperties.AutomationId`; 36 of those were added 2026-09-06, which took the 「no handle at all」 count to zero. **Two kinds of control get nothing on purpose**: anything inside a `DataTemplate` (28 of them), and anything in a UserControl that exists once in markup but many times on screen (`PosterCard`, `EpisodeRow`) — one id shared across N rows makes UIA ambiguous rather than addressable, which is worse than none. `tools/scan-automation-ids.js` prints the whole inventory and is the thing to run after adding an interactive control — the analyzer's own `WUI2020` covers this rule and stays silent on this tree for a reason nobody has pinned down, so it cannot be the tripwire.

**Type sizes come from this project's own seven-step scale, not the six built-in text styles.** `winui-code-review` says no raw `FontSize`; a media client needs 11/12/14/16/20/34/44, which `TitleTextBlockStyle` and its five siblings do not offer. The scale and the named `Eg*Style` text styles are in `src/EmbyNian.Shell/Theme/Styles.xaml` and 34 sites reference them. **What is not defended is the other 55**, which still write a number inline (18 of them sizing a `FontIcon` glyph, which is icon metrics rather than typography) — that half is owed work, not a deviation, and its measured split is in PROGRESS.md.

### Owed work（欠的活）

Four things the skills won outright, **decided by him on 2026-09-05 after being told what each costs**: keying `Palette.xaml`'s dark dictionary `Dark` (done), moving UI strings to `x:Uid` + `.resw` (done — 193 of 194, and `DefaultLanguage=zh-Hans` in the Shell csproj is what makes them resolve at all), packaging as MSIX (**the package is built and signed; installing it is his step**), and dropping the `NavigationView` shell for `winui-design`'s media-app silhouette (the one still to build). Status, scope and each one's fallout live in [PROGRESS.md](PROGRESS.md) — that is work in flight, and a second copy here is how the two disagree. The remaining user-visible unknown is settled: he picked **a top tab strip** for reaching a library once the left pane goes (2026-09-05, from three sketches), recorded in PROGRESS.md, so don't ask again.

**MSIX: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -NoArchive -Msix -Sign`** — from the repo root, and add `-SkipPublish` when the app is open (its own DLLs cannot be deleted, and the cleanup dies half-way through, taking `shaders` with it). The identity lives in `src/EmbyNian.Shell/Package.appxmanifest` (**don't delete it** — `winui-packaging`'s rule, and `publish.ps1` throws without it); the version is patched into it from `Directory.Build.props`. Packaging goes through `winapp package`, not `makeappx` — there is no Windows SDK on this machine. `--skip-pri` is load-bearing: regenerating the PRI would drop the framework merge and produce a package that dies at `App.xaml` (see the `bin\x64\Release` trap below). **`<WindowsPackageType>None</WindowsPackageType>` stays**, so the loose build remains the primary artefact and gates 3 and 4 keep running against `artifacts\publish\win-x64\EmbyNian.exe`; that is why `winui-dev-workflow`'s 「never run the .exe directly」 does not bind here.

**Measured 2026-09-06, once it was actually installed:** the package installs (`EmbyNian_0.0.1.0_x64__mre2em1sb0g7g`), its self-check passes under package identity, and **`%LOCALAPPDATA%` is not redirected** — a full-trust desktop package reads the same `%LOCALAPPDATA%\EmbyNian` as the loose build, so there is nothing to migrate. `AppPaths.PriorRoots` keeps the migration anyway, as the thing that would catch it if that ever stops being true; don't delete it for being unused. The signing certificate lands in `LocalMachine\TrustedPeople` and expires 2027-09-06. `artifacts/` is gitignored, so the PFX cannot be committed — keep it that way, and reuse it with `-CertPath` rather than letting a second one be generated, or the trust he granted is wasted.

**And then it was uninstalled the same day, on his instruction — the package is not to be left installed while iterating.** An installed package is a frozen snapshot: rebuilding and re-publishing never touch it, so its Start-menu tile keeps launching whatever build was packaged, and nothing on screen says so. On 2026-09-06 that tile was clicked while checking a new feature and answered 「你看的是哪一版」 with a build 20 minutes older than the loose one. So `EmbyNian_0.0.1.0_x64__mre2em1sb0g7g` was removed; the signed `.msix` and the PFX stay in `artifacts/`, and the certificate stays trusted, so putting it back is one `Add-AppxPackage`. **The measurement above still stands** — this is about which copy is on screen while verifying, not about whether packaging works. The line that tells the two copies apart is 设置 → 关于 → 构建时间, which reports the running exe's own file time (`AboutFacts.BuiltAt`); read it before believing anything seen in the app, and **verify against the desktop shortcut, which `tools/shortcut.ps1` points at the publish output gate 3 refreshes**.

**Until MSIX lands, three of `winui-dev-workflow`'s four Critical Rules are broken here on purpose, and that is owed work rather than a deviation.** The app is unpackaged (`<WindowsPackageType>None</WindowsPackageType>`), there is no `Package.appxmanifest`, and gates 3 and 4 plus `tools/shot.ps1` all launch `artifacts\publish\win-x64\EmbyNian.exe` directly — against 「NEVER run the .exe directly, always `winapp run`」. Nothing is wrong with the gates as written today; what is wrong is that the packaging job has not been done. **The day it is, that rule starts binding and gate 4 has to launch through the packaged identity instead** — which is why the switch is recorded here and not only in PROGRESS.md, where the rest of that job's fallout lives. The fourth rule (never `AnyCPU`) this project already keeps.

**New UI text goes into `Strings\zh-Hans\Resources.resw` with an `x:Uid`, not into the markup.** That is `winui-code-review`'s globalization rule and this tree now satisfies it, so a literal added back is a regression against a clean file rather than one more of many. Attached properties (`ToolTipService.ToolTip`, `AutomationProperties.*`) take the `<Uid>.[using:Namespace]Type.Property` key form — verified working here by the self-check's 「20 个控件报出了名字」 line, which is also the tripwire if the form is ever mistyped. **`AutomationProperties.AutomationId` is the one that does *not* belong in `.resw`**: it is a test handle rather than text, and a localized handle is a broken handle.

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
- **Collection properties bound to `ItemsSource` stay `{ get; }` with an initializer** and are refilled with `Clear()` + re-add, never reassigned — `winui-code-review`'s own rule, and what makes the `Mode=OneWay` on those bindings a formality rather than a load-bearing subscription.

## The Four Gates（四道闸门）— run all four after any code change

The `dotnet` on `PATH` is unusable: it's 8.0.403 and this project needs .NET 10. The SDK is at `%USERPROFILE%\.dotnet\dotnet.exe` and is not on `PATH`. **Build single-node** — this machine's Windows SDK multi-node workload resolver makes parallel builds fail intermittently with no output at all.

1. Build

   ```
   %USERPROFILE%\.dotnet\dotnet.exe build .\EmbyNian.sln -c Release --no-restore -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -p:MSBuildNodeReuse=false
   ```

   **This gate carries `Microsoft.WindowsAppSDK.Analyzers`.** `winui-code-review` says plain `dotnet build` does not load it and names the way out — the `<Analyzer>` and `<Import>` entries in the project's own `Directory.Build.props` — so that is where they are, pointing at a copy in `tools/analyzers/` rather than at the skill folder a plugin update would replace. Every rule ships at `Warning`, and **this gate is read as 「0 警告 0 错误」**, so a new `WUI####` is a finding to fix rather than noise to silence. Refresh the payload by copying the two files out of the skill again.

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
- **Three lines go red on a maximized window and none of them is a regression**: 「窗口尺寸记得住」 and 「播放帧顶边贴齐」 compare a stored size against a maximized client area (which is born 8 physical pixels inset), and 「屏幕像素」 reports whatever window is actually in front. Set `WindowMaximized` to false in `%LOCALAPPDATA%\EmbyNian\settings.json`, rerun, **put it back**.
- The passing-test count keeps growing as development goes on. **Never write a specific number into a document or a memory** — it will go stale.
- **The UI can be photographed**: `tools/shot.ps1` launches, shoots and closes, `--dump-ui` leaves a shot plus a visual tree, and the self-check leaves one of its own. The procedure, and which themes to shoot, are in the `embynian-verification` skill. **`--dump-ui`'s own PNG does not composite Mica**, so it comes out washed-out grey and cannot be used to judge colour — `tools/shot.ps1` can, and the report's 「屏幕像素」 line reads the real screen.
- Touched colors → one shot per theme. The five live in `src/EmbyNian.Core/Theming/UiThemes.cs` (`emby-dark` default, `oled-black`, `midnight`, `graphite`, `plum`), and **all five are dark** — the sixth, `daylight`, was deleted 2026-09-05 on the user's instruction, and it was the only thing that ever caught a hard-coded shell colour or a system-drawn title bar painting itself dark-on-dark. **Nothing checks for those now.** Why the light half of the derivation deliberately stays behind is in that file's class comment.

## No Real Playback While Verifying（验证时不要真实播放）

The live Emby server (192.168.31.230:8896) is the user's real library, not a fixture — one real playback writes into watch history and resume points.

- **Don't click the middle of a card**: that's the play button floating up on hover, `{ESC}` will not stop what it starts and only killing the process will. Reach a detail page with `--show-detail` / `--show-episode`, or via the home hero's 「详情」 button, the breadcrumb, or a card's bottom title strip — never the artwork itself.
- **Navigate with the app's own command-line switches, never the mouse.** `tools/poke.ps1` clicks wherever the cursor happens to be sitting, and the user's hand is on that same mouse. What programmatic pointer movement does and does not do on this machine was measured four ways on 2026-09-05; **the table is in the `embynian-verification` skill** under 「Moving the pointer, and proving it moved」, together with the one injection that reaches the XAML island and how to prove it did. Read it before writing anything that moves the cursor — the old blanket 「it does nothing」 is what put a no-op in the mouse auto-hide path for a week.
- **A hover-only affordance gets a `--show-*` switch**, not a parked pointer: the pointer can be moved there, but it is gone before the shutter opens.
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
- **`python` and `python3` on `PATH` are Microsoft Store stubs**, not interpreters: they print nothing, exit 0, and that silent success is how 「there is no python」 came to be written here. **A real one is installed** — 3.13.2 under `%LOCALAPPDATA%\Programs\Python\Python313` (and a 3.10 beside it), both on the user `PATH` but shadowed by `WindowsApps`, which comes first. **Reach it with the `py` launcher**, which resolves to 3.13.2. Measured 2026-09-05; it is what makes the media-os skills and `watch` usable.
- PowerShell scripts must be saved as **UTF-8 with BOM**, or Chinese output comes out as mojibake.
- A custom `cut()` function in the shell shadows `/usr/bin/cut`; truncate with `awk '{print substr($0,1,N)}'` instead.
- **`grep -n` and `sed -n` disagree with the file's real line numbers here** — they came out six lines short on a 982-line source file on 2026-09-05, which is how a compiler error at line 575 got read as a line that holds a comment. When a line number matters (an error to chase, an edit to place), get it from the file-reading tool or `node -e` and not from those two.
- **Backslashes do not survive `node -e '...'` from this shell.** A `"\\s"` inside single quotes arrives as `\s`, which JavaScript then reads as a bare `s`, so a regex built by string concatenation silently matches the wrong thing and reports zero hits rather than failing. Use `String.raw`, a regex literal, or write the script to a file first (2026-09-05, after a 19-site edit script quietly did nothing).
- **The WinUI markup compiler fails on a stale cache and succeeds on a retry.** `WMC9999 Xaml Internal Error` — 「未将对象引用设置到对象的实例」 or 「指定的参数已超出有效值的范围」 — is not a fault in the XAML; it hit both the build and the publish gate on 2026-09-05 with no .xaml file touched all session, and both passed on the next run with no code change. The same run can also compile a **stale snapshot of a .cs file** and report an error for code that is no longer there. Rerun the gate once before believing either.
- **Deleting `bin\x64\Release` makes the next publish produce an app that cannot start**, and gate 3 used to say 「验证通过」 anyway. `EmbyNian.pri` has to have the three framework `.pri` files merged into it, and the merge input is 「which `.pri` files are already in the output directory when PRI generation runs」 — unpackaged and non-self-contained, those arrive from `runtimes-framework`, i.e. **only on publish**. So the first publish after a clean produces a 103 KB `EmbyNian.pri` instead of 2.2 MB, the publish is 2 MB light, and the exe dies at `App.xaml` with `Cannot locate resource from 'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'`. **Just run the publish a second time** — the first one left the three files behind. Measured 2026-09-05; `tools/verify-publish.ps1` now fails gate 3 on it instead of letting it through.
- **The mouse cursor cannot be photographed** — not by a GDI screenshot, and not by `winapp ui screenshot` either, `--capture-screen` included (measured 2026-09-05 with the pointer parked inside the window and the crop checked at its own coordinates). `tools/cursor-watch.ps1` is the only way round it and `tools/shot.ps1` cannot be used for it; the how and why are in the `embynian-verification` skill.
- Command-line switches, all of them: `--dump-ui`, `--hide-cursor` (parks the player on screen with its 10 Hz ticker running and nothing else, so the pointer's two-second auto-hide can be watched — plays nothing), `--maximized`, `--play`, `--screen`, `--scroll-end`, `--scroll-half`, `--self-check`, `--show-detail`, `--show-episode`, `--show-library`, `--show-menu` (pops the first home card's 更多 menu open), `--show-osd` (parks the player's overlay on screen without playing a byte; optionally `pinned`, `paused` or `playing`), `--show-rail` (parks the home rail's paging bars on screen — hover-only, and moving the real pointer onto that column did not bring them up), `--show-settings` (optionally a category, e.g. `--show-settings 关于`; defaults to 「界面」), `--theme`. **`--hide-cursor`, `--show-menu`, `--show-osd` and `--show-rail` are each used on their own, never with `--self-check` and never with each other.**

## How To Report（怎么汇报）

The user doesn't read code: settle the implementation details yourself, verify them yourself, and **report in Chinese, in plain words, with no code pasted in**. The only things worth asking about are the ones genuinely his — scope, priority, user-visible behavior, irreversible operations, and **a rule that is currently making this piece of code worse** (see the prime directive). Give him a list he can pick from, not a menu of technical options.




