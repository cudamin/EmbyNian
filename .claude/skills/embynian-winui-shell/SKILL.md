---
name: "embynian-winui-shell"
description: "EmbyNian's WinUI 3 + MVVM terrain: the DI surface and the view-model Attach pattern, the ten pages / eight controls / four dialogs inventory, which pure functions in Core to reuse, which interfaces exist on purpose, the six-theme role table and why literal hex in XAML stays invisible until daylight, and the XAML and pointer traps already paid for. Use when adding, modifying or reviewing C# or XAML in the EmbyNian Emby desktop client (C:\\Users\\89400\\EmbyNian)."
---

# EmbyNian — WinUI 3 shell terrain

EmbyNian is an Emby desktop client: WinUI 3 + Windows App SDK 2.4.0 + .NET 10 + C#, unpackaged, x64, repo at `C:\Users\89400\EmbyNian`. It shipped earlier as **EmbyGearless** and before that **EmbyMpvClient**; those names survive only in the settings-migration path (`Core/Infrastructure/AppPaths.cs`) and in stale artifacts, so anything calling the project by an old name is out of date.

**The rules — the prime directive, the two layering rules, the four gates, credentials, git — are in `CLAUDE.md`, which is loaded whenever this skill is, and it outranks this file on every conflict.** This file keeps no second copy of them; what follows is the terrain those rules apply to. `PROGRESS.md`'s 在途工作 section is the only cross-window handoff: what the current phase is about and what is sitting uncommitted. `src/EmbyNian.Shell/Composition/ShellServices.cs` is the entire DI surface in one file — read it before adding anything.

Playback is the `embynian-playback` skill; the gate procedure is `embynian-verification`; shaders and 画质档位 are `mpv-shader-quality`.

## Three projects

- **`src/EmbyNian.Core`** — zero `PackageReference` and zero `ProjectReference`, only `TargetFramework` plus two `InternalsVisibleTo`. Hence the only layer unit tests reach directly.
- **`src/EmbyNian.Shell`** — the WinUI shell. Nine packages, and WindowsAppSDK is deliberately split into sub-packages (Base / Foundation / InteractiveExperiences / WinUI / DWrite, plus Runtime pinned to `[2.4.0]`) instead of the meta-package, which would drag in ~39 MB of onnxruntime + DirectML. The other three are CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection and System.Security.Cryptography.ProtectedData. `WindowsPackageType=None`, x64, `DISABLE_XAML_GENERATED_MAIN` with a hand-written `Program.cs`.
- **`tests/EmbyNian.Tests`** — zero packages, one project reference (Core), `OutputType=Exe`, hand-written harness. `dotnet run` *is* the test runner. Register new suites in `Program.cs`.

## The shell's terrain

Everything visual is in `src/EmbyNian.Shell/Views/`; view models in `ViewModels/`; DI registrations in `Composition/ShellServices.cs`. **Ten pages** — `ShellPage` (the navigation frame, breadcrumb and library items), `SignInPage`, `HomePage`, `LibraryPage`, `DetailPage`, `SettingsPage`, `ServersPage`, `DashboardPage` (the server console, a WebView2), `DiagnosticsPage`, `PlayerPage`. **Eight hand-built controls** — `PosterCard`, `HomeBanner`, `EpisodeRow`, `ShelfStrip`, `ShelfHead`, `FilterPanel`, `PageSlate`, `AlphaBar`. **Four dialogs** — `CollectionDialog`, `CoverDialog`, `MetadataDialog`, `SubtitleDialog`. Don't assume the list is shorter than this: reasoning from a partial inventory is how a change lands on five pages and misses the other five.

`PlayerPage` is the exception to the whole layout — a sibling `UserControl` of the navigation shell rather than a page inside the `Frame`, a transparent XAML island with mpv's child window showing through. See the `embynian-playback` skill.

## Where capability lives

**`ShellServices.Build(AppPaths)` is the only registration site**: `ValidateOnBuild = true`, `ValidateScopes = true`, and **every registration is `AddSingleton`** — no transient, no scoped. It must be built on the UI thread; `IUiDispatcher` captures that thread and throws 「服务容器必须在界面线程上创建」 otherwise.

Most capability sits in `Core/Emby`, `Core/Playback`, `Core/Mpv` and `Core/Configuration`; `Core/Services/` holds only the handful carved out later (`SettingsService`, `ServerCapabilities`, `FontLibrary`, `PlaybackBackendFactory`, `ShaderStaging`). Judge by which project a type is in and whether it is in the container — never by directory name; moving files to match a directory name changes nothing. `View → ViewModel → Service → outside world` is a direction, not a chain every file has to complete: models, converters, small controls and helpers with no business dependency stay simple.

**Anything with exactly one right answer for a given input belongs in Core as a named pure function pinned by the test project**, service or not, because unit tests only reach Core: a judgment left in a view model is one nobody is watching. "Don't mechanically extract a `Manager` / `Helper` / `Factory` / `IWhatever`" does not soften this — these are named functions with tests holding them down, not a shell wrapped around something that already exists. Reuse these rather than reinventing them:

`HomeLayout` (which home rows, in what order, and only writing settings back when something really changed), `ScreenPlacement` (which monitor, restoring geometry), `ItemArtwork.SamePictures`, `CardSize`, `ImageCachePolicy` and `LruCache` (eviction), `LoadGeneration` (which load is still the current one), `ItemMenu.For` (which menu rows and their wording), `CardStrip`, `WrapLayout`, `HomeCarousel`, `ItemMetadataEdit`, `EpisodeNavigation`, `TrackSelection` / `TrackLanguagePriority`, `StartupArgs`.

## Pages, view models, `Attach`

A WinUI page must have a parameterless constructor and is built by the framework rather than the container, so constructor injection into pages is a dead end. **Pages construct their own view models; dependencies arrive afterwards through `Attach(...)`.** The price is nullable service fields plus a ring of "not attached yet, return early" guards — that isn't a shortcut, write it that way. Two guard shapes are both in use: a `private bool Attached => _settings is not null;` followed by `if (!Attached || … ) return;` (DetailViewModel), or a direct field check `if (_images is not { } images) return;` (HomeViewModel, LibraryViewModel, ServersViewModel, SettingsViewModel).

**`PlayerViewModel` is the only view model in the container**: a film keeps playing behind the library page, so its state has to outlive any navigation. `ShellPage.AttachWindow` resolves it and hands it to the page. `HomePage` marks its single resolution site with a comment saying it is the one place the page resolves anything — keep that habit, because a page reaching into the container from five places is how this rule rots.

Known deviation, not a template: `FilterPanelViewModel` is `new`ed with no `Attach` at all.

## Interfaces

**Don't invent an interface just because a class goes into DI** — with no second implementation and nothing to substitute or isolate, register the concrete class, which is what most registrations here do.

Seven are registered. Only `ISecretProtector` has two production implementations (DPAPI in the shell, a passthrough in Core). The other six — `ISettingsService`, `IServerCapabilities`, `IUiDispatcher`, `IClipboard`, `ISystemLauncher`, `IShellActions` — have one implementation each and no test doubles, and they exist **to narrow reach**: a page that only wants 「每页条数」 must not be able to touch the session, the image cache and the player. **Don't "clean" them into concrete classes**; that spreads everything back within arm's reach. (`CLAUDE.md` names only the first two; the same reasoning covers all six.)

## Code-behind

The rule — pure UI behaviour may stay there, business capability may not — is in `CLAUDE.md`. **The test is "did business logic leak in", not "is there a binding".** Live examples: `ShellPage.xaml.cs` (the `_trail` / `_forward` breadcrumb stacks, library items built from what the server returned), `PlayerPage.Input.cs` (the three window buttons, which exist only on the player's title bar), `PosterCard.xaml.cs` (the hover layer). The counter-example: page-level busy/notice is *not* a code-behind toggle — it is a derived property on `PageViewModel` with `[NotifyPropertyChangedFor]`.

The one known exception, `Views/ItemCommands.cs`, is a static class, so it structurally cannot be in the container; it takes an `EmbySession` as a parameter through every method and drives the card context menu's commands. It does **not** hand-roll HTTP: every round trip goes through `session.ExecuteAsync` and then `EmbyClient`, funnelled through `AskAsync` / `TellAsync` / `RunAsync`. The field rules for metadata editing already moved out into `Core/Emby/ItemMetadataEdit.cs` and are pinned by tests there. **Old debt, not a template — don't copy the shape**, and don't refactor it into a service unless the user asked for that.

## Colours: the role table, and why literal hex hides

The six themes live in `src/EmbyNian.Core/Theming/UiThemes.cs`. Each is **five colour values (window, surface, surfaceAlt, text, accent) expanded into a whole role table** (`UiTheme` / `ThemeColor`), with danger / warning / info overridable per theme. `src/EmbyNian.Shell/Theme/ThemeHost.cs` pushes that table into application-level brushes at runtime; `Theme/Palette.xaml` **only covers the first frame**, and the pushed table is what actually takes effect. `Theme/Styles.xaml` holds the shared styles.

**A new colour must go through the role table. Never write a literal hex in XAML.** A hard-coded colour is invisible to the theme engine — and invisible to *you*, because it looks fine in the four dark themes and only misbehaves once you switch to `daylight`. That is also the fastest way to check whether a colour is right: take one shot with `--theme daylight`.

## XAML traps already paid for

- **`x:Load` breaks animations.** A `Storyboard`'s `TargetName` is resolved against the namescope at the moment `Begin()` runs, and a deferred element isn't in the scope yet, so the animation throws outright. The player's pause-pulse probe (`ProbePulse`) exists to pin exactly this. Before adding `x:Load` to anything to save startup time, confirm no `Storyboard` points at it and no code reaches it by name.
- **Don't lazy-load an element that code reaches by name.** The few milliseconds saved buy a field of null references.
- **The playback title bar is client area**, so dragging the window and minimise / maximise / close all have to be implemented by the page itself. Don't treat it as an ordinary title bar — the `InputNonClientPointerSource` machinery only covers browse state. Withdrawing a region declaration (`ClearRegionRects`, or an empty array) turns the whole strip permanently into client area, so after watching one film the window can no longer be dragged; the self-check asks `WM_NCHITTEST` about this on purpose.
- **Two pointer facts that keep biting.** WinUI raises `PointerMoved` when the content under a *stationary* pointer changes — treat that as real movement and auto-hide never fires. And `PointerExited` fires when the pointer merely moves onto a child of the same control — treat that as "left the window" and the overlay blinks out the moment a hand reaches for it. To decide whether the pointer really left the window, **ask the system**; don't read the coordinates off the event.

## Verifying a UI change

**Layout arithmetic goes down into Core, where it can be tested** — that is where `CardSizeTests`, `CardStripTests`, `WrapLayoutTests`, `HomeCarouselTests`, `ThemeTests` and `ScreenTests` came from. If it can be computed, don't check it by eye. Everything else — the four gates, `--self-check --dump-ui`, per-page photography with `tools/shot.ps1`, which themes to shoot when colours moved — is the `embynian-verification` skill.
